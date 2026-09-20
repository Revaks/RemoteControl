#include <winsock2.h>
#include <windows.h>
#include <wincrypt.h>

#include <winldap.h>
#include <dsgetdc.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "config.h"
#include "auth.h"
#include "eventlog.h"

#pragma comment(lib, "wldap32.lib")
#pragma comment(lib, "netapi32.lib")
#pragma comment(lib, "crypt32.lib")

// ---------- вспомогательные ----------

// Экранирование спецсимволов LDAP-фильтра (RFC 4515).
static void ldap_filter_escape(const wchar_t* in, wchar_t* out, size_t outLen)
{
    size_t o = 0;
    for (size_t i = 0; in[i] != L'\0' && o + 4 < outLen; i++)
    {
        switch (in[i])
        {
            case L'\\': out[o++] = L'\\'; out[o++] = L'5'; out[o++] = L'c'; break;
            case L'*':  out[o++] = L'\\'; out[o++] = L'2'; out[o++] = L'a'; break;
            case L'(':  out[o++] = L'\\'; out[o++] = L'2'; out[o++] = L'8'; break;
            case L')':  out[o++] = L'\\'; out[o++] = L'2'; out[o++] = L'9'; break;
            case L'\0': break;
            default:    out[o++] = in[i]; break;
        }
    }
    out[o] = L'\0';
}

static int check_group_membership(LDAP* ld, const wchar_t* baseDn, const wchar_t* userDn, const wchar_t* groupDn)
{
    wchar_t escUser[1024];
    wchar_t escGroup[1024];
    ldap_filter_escape(userDn, escUser, _countof(escUser));
    ldap_filter_escape(groupDn, escGroup, _countof(escGroup));

    wchar_t filter[2200];
    // LDAP_MATCHING_RULE_IN_CHAIN учитывает вложенные группы.
    swprintf_s(filter, _countof(filter),
        L"(&(objectClass=group)(distinguishedName=%s)(member:1.2.840.113556.1.4.1941:=%s))",
        escGroup, escUser);

    LDAPMessage* res = NULL;
    ULONG rc = ldap_search_sW(ld, (PWCHAR)baseDn, LDAP_SCOPE_SUBTREE, filter, NULL, 0, &res);
    if (rc != LDAP_SUCCESS)
        return -1;

    int found = ldap_count_entries(ld, res) > 0;
    ldap_msgfree(res);
    return found ? 1 : 0;
}

// Читает MULTI_SZ из реестра. Возвращает NULL, если не задано.
static wchar_t* read_allowed_groups(void)
{
    HKEY key;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, RC_REG_PARAMETERS_KEY, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS)
        return NULL;

    wchar_t* groups = NULL;
    DWORD type = 0, size = 0;
    if (RegQueryValueExW(key, RC_REG_VALUE_ALLOWED_GROUPS, NULL, &type, NULL, &size) == ERROR_SUCCESS &&
        type == REG_MULTI_SZ && size > 4)
    {
        groups = (wchar_t*)malloc(size);
        if (groups != NULL)
        {
            if (RegQueryValueExW(key, RC_REG_VALUE_ALLOWED_GROUPS, NULL, &type,
                    (LPBYTE)groups, &size) != ERROR_SUCCESS)
            {
                free(groups);
                groups = NULL;
            }
        }
    }

    RegCloseKey(key);
    return groups;
}

// ---------- основная проверка ----------

int auth_check_certificate(PCCERT_CONTEXT clientCert, wchar_t* upn, size_t upnLen)
{
    // 1. UPN из SAN сертификата.
    DWORD chars = CertGetNameStringW(clientCert, CERT_NAME_UPN_TYPE, 0, NULL, NULL, 0);
    if (chars <= 1)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1200, L"В сертификате клиента нет UPN (SAN)");
        return -1;
    }

    wchar_t* tmp = (wchar_t*)malloc(chars * sizeof(wchar_t));
    if (tmp == NULL)
        return -1;

    if (CertGetNameStringW(clientCert, CERT_NAME_UPN_TYPE, 0, NULL, tmp, chars) <= 1)
    {
        free(tmp);
        rc_event_log(EVENTLOG_ERROR_TYPE, 1200, L"Не удалось извлечь UPN из сертификата");
        return -1;
    }

    wcsncpy_s(upn, upnLen, tmp, _TRUNCATE);
    free(tmp);

    // 2. Список разрешённых групп из реестра.
    wchar_t* groups = read_allowed_groups();
    if (groups == NULL || groups[0] == L'\0')
    {
        // Группы не настроены: доступ по факту предъявления сертификата домена.
        // Для продакшена обязательно настроить AllowedGroups через GPO/реестр.
        rc_event_log(EVENTLOG_WARNING_TYPE, 1201,
            L"AllowedGroups не настроен — доступ разрешён любому сертификату домена. UPN=%s", upn);
        free(groups);
        return 0;
    }

    // 3. Ищем контроллер домена.
    PDOMAIN_CONTROLLER_INFOW dcInfo = NULL;
    DWORD rc = DsGetDcNameW(NULL, NULL, NULL, NULL, DS_RETURN_DNS_NAME, &dcInfo);
    if (rc != ERROR_SUCCESS)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1202, L"DsGetDcNameW: %lu", rc);
        free(groups);
        return -1;
    }

    // DomainControllerName имеет вид "\\DC01.corp.local".
    wchar_t dcHost[256];
    swprintf_s(dcHost, _countof(dcHost), L"%s", dcInfo->DomainControllerName);
    wchar_t* p = dcHost;
    while (*p == L'\\')
        p++;

    LDAP* ld = ldap_initW(p, LDAP_PORT);
    if (ld == NULL)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1210, L"ldap_initW(%s) failed", p);
        NetApiBufferFree(dcInfo);
        free(groups);
        return -1;
    }

    ULONG version = LDAP_VERSION3;
    ldap_set_optionW(ld, LDAP_OPT_PROTOCOL_VERSION, &version);

    int result = -1;

    ULONG lrc = ldap_connect(ld, NULL);
    if (lrc != LDAP_SUCCESS)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1211, L"ldap_connect(%s): %lu", p, lrc);
        goto cleanup;
    }
    // Бинд учёткой машины (служба работает от SYSTEM) через SSPI.
    lrc = ldap_bind_sW(ld, NULL, NULL, LDAP_AUTH_NEGOTIATE);
    if (lrc != LDAP_SUCCESS)
    {
        rc_event_log(EVENTLOG_ERROR_TYPE, 1212, L"ldap_bind_sW: %lu", lrc);
        goto cleanup;
    }

    // База поиска — defaultNamingContext из Root DSE (NULL/"" здесь не подходит:
    // поддерево от Root DSE даёт LDAP_NO_SUCH_OBJECT).
    wchar_t baseDn[512] = L"";
    {
        wchar_t* baseAttrs[] = { L"defaultNamingContext", NULL };
        LDAPMessage* baseRes = NULL;
        ULONG brc = ldap_search_sW(ld, NULL, LDAP_SCOPE_BASE, L"(objectClass=*)", baseAttrs, 0, &baseRes);
        if (brc == LDAP_SUCCESS)
        {
            LDAPMessage* entry = ldap_first_entry(ld, baseRes);
            if (entry != NULL)
            {
                wchar_t** vals = ldap_get_valuesW(ld, entry, L"defaultNamingContext");
                if (vals != NULL)
                {
                    wcsncpy_s(baseDn, _countof(baseDn), vals[0], _TRUNCATE);
                    ldap_value_freeW(vals);
                }
            }
            ldap_msgfree(baseRes);
        }

        if (baseDn[0] == L'\0')
        {
            // Резервный вариант: строим DC= из доменного имени (corp.local -> DC=corp,DC=local).
            wchar_t tmp[256];
            wcsncpy_s(tmp, _countof(tmp), dcInfo->DomainName, _TRUNCATE);
            wcscat_s(baseDn, _countof(baseDn), L"DC=");
            for (wchar_t* q = tmp; *q != L'\0'; q++)
            {
                if (*q == L'.')
                    wcscat_s(baseDn, _countof(baseDn), L",DC=");
                else
                {
                    wchar_t ch[2] = { *q, L'\0' };
                    wcscat_s(baseDn, _countof(baseDn), ch);
                }
            }
        }
    }

    // Находим пользователя по UPN.
    {
        wchar_t escUpn[512];
        ldap_filter_escape(upn, escUpn, _countof(escUpn));

        wchar_t filter[768];
        swprintf_s(filter, _countof(filter),
            L"(&(objectClass=user)(userPrincipalName=%s))", escUpn);

        wchar_t* attrs[] = { L"distinguishedName", NULL };
        LDAPMessage* res = NULL;
        ULONG src = ldap_search_sW(ld, (PWCHAR)baseDn, LDAP_SCOPE_SUBTREE, filter, attrs, 0, &res);
        if (src != LDAP_SUCCESS)
        {
            rc_event_log(EVENTLOG_ERROR_TYPE, 1213, L"ldap_search_sW(user): %lu", src);
            goto cleanup;
        }

        LDAPMessage* entry = ldap_first_entry(ld, res);
        if (entry == NULL)
        {
            rc_event_log(EVENTLOG_ERROR_TYPE, 1203, L"Пользователь %s не найден в AD", upn);
            ldap_msgfree(res);
            goto cleanup;
        }

        wchar_t* userDn = ldap_get_dnW(ld, entry);

        // Проверяем членство для каждой разрешённой группы.
        const wchar_t* g = groups;
        while (*g != L'\0')
        {
            int found = check_group_membership(ld, baseDn, userDn, g);
            if (found == 1)
            {
                result = 0;
                rc_event_log(EVENTLOG_INFORMATION_TYPE, 1204,
                    L"Доступ разрешён: UPN=%s, группа=%s", upn, g);
                break;
            }
            g += wcslen(g) + 1;
        }

        if (result != 0)
            rc_event_log(EVENTLOG_WARNING_TYPE, 1205, L"Доступ запрещён: UPN=%s", upn);

        if (userDn != NULL)
            ldap_memfreeW(userDn);
        ldap_msgfree(res);
    }

cleanup:
    ldap_unbind(ld);
    NetApiBufferFree(dcInfo);
    free(groups);
    return result;
}
