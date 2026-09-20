#pragma once

#include <winsock2.h>
#include <windows.h>

#define SECURITY_WIN32
#include <security.h>
#include <schannel.h>

typedef struct tls_conn tls_conn;

// Находит сертификат машины (LocalMachine\MY) с закрытым ключом и EKU Server Authentication.
PCCERT_CONTEXT tls_find_machine_certificate(void);

// Полный TLS-хендшейк на сокете. Требует клиентский сертификат (mTLS).
// Возвращает 0 при успехе, -1 при ошибке.
int tls_server_accept(SOCKET sock, PCCERT_CONTEXT serverCert, tls_conn** out);

// Читает расшифрованные данные. Возвращает >0 (байт), 0 — соединение закрыто, -1 — ошибка.
int tls_read(tls_conn* conn, void* buf, int len);

// Шифрует и отправляет все данные. Возвращает 0 или -1.
int tls_write(tls_conn* conn, const void* buf, int len);

// Сертификат клиента (валиден пока жив tls_conn).
PCCERT_CONTEXT tls_get_peer_certificate(tls_conn* conn);

// Жёстко рвёт соединение (для разблокировки потоков ретранслятора).
void tls_abort(tls_conn* conn);

// Освобождает ресурсы и закрывает сокет.
void tls_close(tls_conn* conn);
