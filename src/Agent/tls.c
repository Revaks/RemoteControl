#include <winsock2.h>
#include <windows.h>

#define SECURITY_WIN32
#include <security.h>
#include <schannel.h>
#include <ncrypt.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "config.h"
#include "tls.h"
#include "eventlog.h"

#pragma comment(lib, "secur32.lib")
#pragma comment(lib, "crypt32.lib")

struct tls_conn
{
    SOCKET sock;

    CredHandle cred;
    CtxtHandle ctx;
    BOOL ctxValid;

    SecPkgContext_StreamSizes sizes;
    PCCERT_CONTEXT peerCert;

    // Накопленный (ещё не расшифрованный) шифротекст.
    BYTE* cipherIn;
    int   cipherInLen;
    int   cipherInCap;

    // Расшифрованные, но ещё не отданные вызывающему байты.
    BYTE* plainPending;
    int   plainPendingLen;
    int   plainPendingCap;
};

// ---------- вспомогательные ----------

static int send_all(SOCKET s, const void* buf, int len)
{
    const char* p = (const char*)buf;
    while (len > 0)
    {
        int n = send(s, p, len, 0);
        if (n == SOCKET_ERROR)
            return -1;
        p += n;
        len -= n;
    }
    return 0;
}

static int buf_reserve(BYTE** buf, int* cap, int needed)
{
    if (*cap >= needed)
        return 0;

    int newCap = *cap > 0 ? *cap : 4096;
    while (newCap < needed)
        newCap *= 2;

    BYTE* p = (BYTE*)realloc(*buf, (size_t)newCap);
    if (p == NULL)
        return -1;

    *buf = p;
    *cap = newCap;
    return 0;
}

// Есть ли у сертификата закрытый ключ (поддерживаем CNG и legacy CSP).
static BOOL cert_has_private_key(PCCERT_CONTEXT cert)
{
    DWORD keySpec = 0;
    BOOL callerFree = FALSE;
    HCRYPTPROV_OR_NCRYPT_KEY_HANDLE hKey = 0;

    // CNG-ключи требуют этого флага.
    if (CryptAcquireCertificatePrivateKey(cert,
            CRYPT_ACQUIRE_ONLY_NCRYPT_KEY_FLAG | CRYPT_ACQUIRE_SILENT_FLAG,
            NULL, &hKey, &keySpec, &callerFree))
    {
        NCryptFreeObject(hKey);
        return TRUE;
    }

    // Legacy CSP.
    if (CryptAcquireCertificatePrivateKey(cert, CRYPT_ACQUIRE_SILENT_FLAG,
            NULL, &hKey, &keySpec, &callerFree))
    {
        if (keySpec == CERT_NCRYPT_KEY_SPEC)
            NCryptFreeObject(hKey);
        else
            CryptReleaseContext(hKey, 0);
        return TRUE;
    }

    return FALSE;
}

PCCERT_CONTEXT tls_find_machine_certificate(void)
{
    // Хранилище компьютера (LocalMachine\MY): CertOpenSystemStore открывает
    // CurrentUser\MY, что для службы (SYSTEM) даёт пустое хранилище.
    HCERTSTORE store = CertOpenStore(
        CERT_STORE_PROV_SYSTEM, 0, 0,
        CERT_SYSTEM_STORE_LOCAL_MACHINE, L"MY");
    if (store == NULL)
        return NULL;

    PCCERT_CONTEXT found = NULL;
    PCCERT_CONTEXT cur = NULL;

    while ((cur = CertEnumCertificatesInStore(store, cur)) != NULL)
    {
        // 1. Должен быть закрытый ключ.
        if (!cert_has_private_key(cur))
            continue;

        // 2. EKU обязателен и должен включать Server Authentication (1.3.6.1.5.5.7.3.1),
        //    иначе можно подхватить корневой/служебный сертификат без EKU.
        DWORD cb = 0;
        BOOL serverAuth = FALSE;
        if (CertGetEnhancedKeyUsage(cur, 0, NULL, &cb) && cb > 0)
        {
            PCERT_ENHKEY_USAGE usage = (PCERT_ENHKEY_USAGE)malloc(cb);
            if (usage != NULL)
            {
                if (CertGetEnhancedKeyUsage(cur, 0, usage, &cb))
                {
                    for (DWORD i = 0; i < usage->cUsageIdentifier; i++)
                    {
                        if (strcmp(usage->rgpszUsageIdentifier[i], szOID_PKIX_KP_SERVER_AUTH) == 0)
                        {
                            serverAuth = TRUE;
                            break;
                        }
                    }
                }
                free(usage);
            }
        }
        if (!serverAuth)
            continue;

        found = CertDuplicateCertificateContext(cur);
        break;
    }

    CertCloseStore(store, 0);
    return found;
}

// ---------- хендшейк ----------

int tls_server_accept(SOCKET sock, PCCERT_CONTEXT serverCert, tls_conn** out)
{
    tls_conn* c = (tls_conn*)calloc(1, sizeof(tls_conn));
    if (c == NULL)
        return -1;

    c->sock = sock;

    // Учётные данные: сертификат машины, TLS 1.2/1.3, проверка цепочки клиента.
    SCHANNEL_CRED sc;
    ZeroMemory(&sc, sizeof(sc));
    sc.dwVersion = SCHANNEL_CRED_VERSION;
    sc.cCreds = 1;
    sc.paCred = &serverCert;
    sc.grbitEnabledProtocols = SP_PROT_TLS1_2_SERVER | SP_PROT_TLS1_3_SERVER;
    sc.dwFlags = SCH_CRED_NO_DEFAULT_CREDS |
                 SCH_CRED_REVOCATION_CHECK_CHAIN |
                 SCH_CRED_CACHE_ONLY_URL_RETRIEVAL_ON_CREATE;

    SECURITY_STATUS ss = AcquireCredentialsHandleW(NULL, (SEC_WCHAR*)UNISP_NAME_W,
        SECPKG_CRED_INBOUND, NULL, &sc, NULL, NULL, &c->cred, NULL);
    if (ss != SEC_E_OK)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1100, L"AcquireCredentialsHandle: 0x%08x", (unsigned)ss);
        free(c);
        return -1;
    }

    DWORD ctxReq = ASC_REQ_CONFIDENTIALITY | ASC_REQ_STREAM |
                   ASC_REQ_REPLAY_DETECT | ASC_REQ_SEQUENCE_DETECT |
                   ASC_REQ_MUTUAL_AUTH; // требовать клиентский сертификат
    // Без ASC_REQ_ALLOCATE_MEMORY: Schannel расшифровывает in-place в наши
    // буферы, и tls_read может корректно хранить остаток шифротекста.

    BOOL haveCtx = FALSE;

    for (;;)
    {
        // Читаем очередную порцию шифротекста.
        BYTE tmp[RC_TLS_RECORD_MAX + 1024];
        int n = recv(c->sock, (char*)tmp, sizeof(tmp), 0);
        if (n <= 0)
            goto fail;

        if (buf_reserve(&c->cipherIn, &c->cipherInCap, c->cipherInLen + n) != 0)
            goto fail;
        memcpy(c->cipherIn + c->cipherInLen, tmp, n);
        c->cipherInLen += n;

        SecBuffer inBuf[2];
        inBuf[0].pvBuffer = c->cipherIn;
        inBuf[0].cbBuffer = (ULONG)c->cipherInLen;
        inBuf[0].BufferType = SECBUFFER_TOKEN;
        inBuf[1].pvBuffer = NULL;
        inBuf[1].cbBuffer = 0;
        inBuf[1].BufferType = SECBUFFER_EMPTY;

        SecBufferDesc inDesc;
        inDesc.ulVersion = SECBUFFER_VERSION;
        inDesc.cBuffers = 2;
        inDesc.pBuffers = inBuf;

        BYTE outToken[RC_TLS_RECORD_MAX + 1024];
        SecBuffer outBuf;
        ZeroMemory(&outBuf, sizeof(outBuf));
        outBuf.pvBuffer = outToken;
        outBuf.cbBuffer = sizeof(outToken);
        outBuf.BufferType = SECBUFFER_TOKEN;
        SecBufferDesc outDesc;
        outDesc.ulVersion = SECBUFFER_VERSION;
        outDesc.cBuffers = 1;
        outDesc.pBuffers = &outBuf;

        DWORD ctxAttr = 0;
        ss = AcceptSecurityContext(&c->cred,
            haveCtx ? &c->ctx : NULL,
            &inDesc, ctxReq, SECURITY_NATIVE_DREP,
            haveCtx ? NULL : &c->ctx,
            &outDesc, &ctxAttr, NULL);

        // Отправляем токен клиенту, если есть.
        if (outBuf.cbBuffer > 0)
            send_all(c->sock, outBuf.pvBuffer, outBuf.cbBuffer);

        if (ss == SEC_E_INCOMPLETE_MESSAGE)
        {
            // Нужно больше данных.
            continue;
        }

        if (ss == SEC_I_CONTINUE_NEEDED || ss == SEC_E_OK)
        {
            haveCtx = TRUE;

            // Убираем потреблённые байты, сохраняем лишние (остаток шифротекста
            // остаётся в cipherIn и будет обработан в tls_read через DecryptMessage).
            int consumed = (int)inBuf[0].cbBuffer;
            if (consumed < 0 || consumed > c->cipherInLen)
                consumed = c->cipherInLen;
            memmove(c->cipherIn, c->cipherIn + consumed, c->cipherInLen - consumed);
            c->cipherInLen -= consumed;

            if (ss == SEC_E_OK)
                break;
            continue;
        }

        rc_event_log(EVENTLOG_ERROR_TYPE, 1101, L"TLS handshake: 0x%08x", (unsigned)ss);
        goto fail;
    }

    c->ctxValid = TRUE;

    if (QueryContextAttributesW(&c->ctx, SECPKG_ATTR_STREAM_SIZES, &c->sizes) != SEC_E_OK)
        goto fail;

    // Клиентский сертификат обязателен (mTLS).
    if (QueryContextAttributesW(&c->ctx, SECPKG_ATTR_REMOTE_CERT_CONTEXT, &c->peerCert) != SEC_E_OK ||
        c->peerCert == NULL)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1102, L"Клиент не предъявил сертификат");
        goto fail;
    }

    *out = c;
    return 0;

fail:
    if (c->ctxValid)
        DeleteSecurityContext(&c->ctx);
    FreeCredentialsHandle(&c->cred);
    free(c->cipherIn);
    free(c->plainPending);
    free(c);
    // ВАЖНО: всегда возвращаем ошибку. Ранее здесь возвращался rc, который
    // успевал стать 0 после SEC_E_OK — вызывающий код получал «успех» при
    // неинициализированном *out и падал на NULL-указателе.
    return -1;
}

// ---------- данные ----------

int tls_read(tls_conn* c, void* buf, int len)
{
    // Сначала отдаём ранее расшифрованные данные.
    if (c->plainPendingLen > 0)
    {
        int n = len < c->plainPendingLen ? len : c->plainPendingLen;
        memcpy(buf, c->plainPending, n);
        memmove(c->plainPending, c->plainPending + n, c->plainPendingLen - n);
        c->plainPendingLen -= n;
        return n;
    }

    for (;;)
    {
        // Сначала гарантируем наличие шифротекста: не вызываем DecryptMessage
        // с пустым входным буфером.
        if (c->cipherInLen == 0)
        {
            BYTE tmp[RC_TLS_RECORD_MAX + 1024];
            int n = recv(c->sock, (char*)tmp, sizeof(tmp), 0);
            if (n <= 0)
                return n; // 0 — закрыто, -1 — ошибка

            if (buf_reserve(&c->cipherIn, &c->cipherInCap, n) != 0)
                return -1;
            memcpy(c->cipherIn, tmp, n);
            c->cipherInLen = n;
        }

        SecBuffer bufs[4];
        bufs[0].pvBuffer = c->cipherIn;
        bufs[0].cbBuffer = (ULONG)c->cipherInLen;
        bufs[0].BufferType = SECBUFFER_DATA;
        bufs[1].pvBuffer = NULL; bufs[1].cbBuffer = 0; bufs[1].BufferType = SECBUFFER_EMPTY;
        bufs[2].pvBuffer = NULL; bufs[2].cbBuffer = 0; bufs[2].BufferType = SECBUFFER_EMPTY;
        bufs[3].pvBuffer = NULL; bufs[3].cbBuffer = 0; bufs[3].BufferType = SECBUFFER_EMPTY;

        SecBufferDesc desc;
        desc.ulVersion = SECBUFFER_VERSION;
        desc.cBuffers = 4;
        desc.pBuffers = bufs;

        SECURITY_STATUS ss = DecryptMessage(&c->ctx, &desc, 0, NULL);

        if (ss == SEC_E_INCOMPLETE_MESSAGE)
        {
            BYTE tmp[RC_TLS_RECORD_MAX + 1024];
            int n = recv(c->sock, (char*)tmp, sizeof(tmp), 0);
            if (n <= 0)
                return n; // 0 — закрыто, -1 — ошибка

            if (buf_reserve(&c->cipherIn, &c->cipherInCap, c->cipherInLen + n) != 0)
                return -1;
            memcpy(c->cipherIn + c->cipherInLen, tmp, n);
            c->cipherInLen += n;
            continue;
        }

        if (ss != SEC_E_OK)
            return -1;

        // Schannel может вернуть расшифрованные данные в любом из буферов,
        // помеченном SECBUFFER_DATA (обычно bufs[1] для stream-протоколов;
        // в некоторых версиях — bufs[0]). Остаток шифротекста — SECBUFFER_EXTRA.
        BYTE* plain = NULL;
        int plainLen = 0;
        int extra = 0;
        for (int i = 0; i < 4; i++)
        {
            if (bufs[i].BufferType == SECBUFFER_DATA)
            {
                plain = (BYTE*)bufs[i].pvBuffer;
                plainLen = (int)bufs[i].cbBuffer;
            }
            else if (bufs[i].BufferType == SECBUFFER_EXTRA)
            {
                extra += (int)bufs[i].cbBuffer;
            }
        }

        // ВАЖНО: сначала копируем plaintext наружу — он лежит внутри cipherIn,
        // и последующий memmove остатка шифротекста затрёт эти байты.
        int resultLen = 0;
        if (plainLen > 0)
        {
            resultLen = len < plainLen ? len : plainLen;
            memcpy(buf, plain, resultLen);
            if (plainLen > resultLen)
            {
                if (buf_reserve(&c->plainPending, &c->plainPendingCap,
                        c->plainPendingLen + (plainLen - resultLen)) == 0)
                {
                    memcpy(c->plainPending + c->plainPendingLen,
                           plain + resultLen, plainLen - resultLen);
                    c->plainPendingLen += plainLen - resultLen;
                }
            }
        }

        // Теперь переносим неиспользованный шифротекст в начало cipherIn.
        if (extra > 0)
        {
            // SECBUFFER_EXTRA указывает на хвост исходного буфера (in-place),
            // но безопаснее копировать из фактического указателя extra-буфера.
            BYTE* extraPtr = NULL;
            for (int i = 0; i < 4; i++)
            {
                if (bufs[i].BufferType == SECBUFFER_EXTRA)
                {
                    extraPtr = (BYTE*)bufs[i].pvBuffer;
                    break;
                }
            }
            if (extraPtr != NULL)
                memmove(c->cipherIn, extraPtr, extra);
            else
                memmove(c->cipherIn, c->cipherIn + (c->cipherInLen - extra), extra);
            c->cipherInLen = extra;
        }
        else
        {
            c->cipherInLen = 0;
        }

        if (plainLen <= 0)
            continue; // например, пустая TLS-запись

        return resultLen;
    }
}

int tls_write(tls_conn* c, const void* buf, int len)
{
    int maxMsg = (int)c->sizes.cbMaximumMessage;
    if (maxMsg <= 0 || maxMsg > RC_TLS_RECORD_MAX)
        maxMsg = RC_TLS_RECORD_MAX;

    const BYTE* p = (const BYTE*)buf;

    while (len > 0)
    {
        int chunk = len < maxMsg ? len : maxMsg;

        BYTE header[256];
        BYTE trailer[256];
        if (c->sizes.cbHeader > sizeof(header) || c->sizes.cbTrailer > sizeof(trailer))
            return -1;

        SecBuffer bufs[4];
        bufs[0].pvBuffer = header;
        bufs[0].cbBuffer = c->sizes.cbHeader;
        bufs[0].BufferType = SECBUFFER_STREAM_HEADER;
        bufs[1].pvBuffer = (void*)p;
        bufs[1].cbBuffer = (ULONG)chunk;
        bufs[1].BufferType = SECBUFFER_DATA;
        bufs[2].pvBuffer = trailer;
        bufs[2].cbBuffer = c->sizes.cbTrailer;
        bufs[2].BufferType = SECBUFFER_STREAM_TRAILER;
        bufs[3].pvBuffer = NULL; bufs[3].cbBuffer = 0; bufs[3].BufferType = SECBUFFER_EMPTY;

        SecBufferDesc desc;
        desc.ulVersion = SECBUFFER_VERSION;
        desc.cBuffers = 4;
        desc.pBuffers = bufs;

        SECURITY_STATUS ss = EncryptMessage(&c->ctx, 0, &desc, 0);
        if (ss != SEC_E_OK)
            return -1;

        if (send_all(c->sock, bufs[0].pvBuffer, (int)bufs[0].cbBuffer) != 0)
            return -1;
        if (send_all(c->sock, bufs[1].pvBuffer, (int)bufs[1].cbBuffer) != 0)
            return -1;
        if (send_all(c->sock, bufs[2].pvBuffer, (int)bufs[2].cbBuffer) != 0)
            return -1;

        p += chunk;
        len -= chunk;
    }

    return 0;
}

// ---------- прочее ----------

PCCERT_CONTEXT tls_get_peer_certificate(tls_conn* c)
{
    return c->peerCert;
}

void tls_abort(tls_conn* c)
{
    if (c != NULL && c->sock != INVALID_SOCKET)
    {
        // Резкое закрытие разблокирует recv/send в потоках ретранслятора.
        shutdown(c->sock, SD_BOTH);
        closesocket(c->sock);
        c->sock = INVALID_SOCKET;
    }
}

void tls_close(tls_conn* c)
{
    if (c == NULL)
        return;

    if (c->ctxValid)
        DeleteSecurityContext(&c->ctx);
    FreeCredentialsHandle(&c->cred);
    if (c->sock != INVALID_SOCKET)
        closesocket(c->sock);
    free(c->cipherIn);
    free(c->plainPending);
    free(c);
}
