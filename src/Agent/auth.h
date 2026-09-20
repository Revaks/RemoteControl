#pragma once

#include <winsock2.h>
#include <windows.h>
#include <wincrypt.h>

// Проверяет сертификат клиента:
//   1) извлекает UPN из SAN;
//   2) правила доступа (достаточно любого включённого):
//      - AllowedGroups (MULTI_SZ) — членство в перечисленных группах AD через LDAP;
//      - AllowDomainUsers (DWORD, по умолчанию выключено) — любой включённый пользователь
//        домена: достаточно, чтобы UPN из сертификата совпал с учётной записью в AD;
//      - AllowLocalAdmins (DWORD, по умолчанию включено) — учётка входит в локальную
//        группу Administrators этой машины (проверка через SAM/RPC, без AD-групп).
//      Учётные записи, отключённые в AD, не допускаются ни по одному правилу.
// Возвращает 0 при успехе; при успехе заполняет upn (до upnLen символов).
int auth_check_certificate(PCCERT_CONTEXT clientCert, wchar_t* upn, size_t upnLen);
