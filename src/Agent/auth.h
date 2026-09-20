#pragma once

#include <winsock2.h>
#include <windows.h>
#include <wincrypt.h>

// Проверяет сертификат клиента:
//   1) извлекает UPN из SAN;
//   2) если в реестре заданы AllowedGroups (DN групп AD) — проверяет членство через LDAP.
// Возвращает 0 при успехе; при успехе заполняет upn (до upnLen символов).
int auth_check_certificate(PCCERT_CONTEXT clientCert, wchar_t* upn, size_t upnLen);
