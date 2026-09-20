#pragma once

// Порядок важен: winsock2.h до windows.h (иначе конфликт winsock/winsock2)
#include <winsock2.h>
#include <windows.h>

// Имена и параметры по умолчанию
#define RC_AGENT_SERVICE_NAME      L"RemoteControlAgent"
#define RC_AGENT_DISPLAY_NAME      L"Remote Control Agent"
#define RC_AGENT_DEFAULT_PORT      5900
#define RC_EVENTLOG_SOURCE         L"RemoteControlAgent"

// Параметры в реестре (пишутся GPO или вручную):
//   HKLM\SYSTEM\CurrentControlSet\Services\RemoteControlAgent\Parameters
#define RC_REG_PARAMETERS_KEY          L"SYSTEM\\CurrentControlSet\\Services\\RemoteControlAgent\\Parameters"
#define RC_REG_VALUE_PORT              L"ListenPort"          // DWORD, порт
#define RC_REG_VALUE_ALLOWED_GROUPS    L"AllowedGroups"       // MULTI_SZ, DN групп AD, которым разрешён доступ
#define RC_REG_VALUE_REQUIRE_CLIENT_CERT L"RequireClientCert" // DWORD, 1 = требовать mTLS
// DWORD, 1 = доступ разрешён также учётной записи, которая входит в локальную
// группу Administrators этой машины (даже если её нет в AllowedGroups или в AD).
// По умолчанию (значение отсутствует) — включено.
#define RC_REG_VALUE_ALLOW_LOCAL_ADMINS L"AllowLocalAdmins"

// Мьютекс одиночного экземпляра службы
#define RC_SERVICE_MUTEX_NAME          L"Global\\RemoteControlAgent.ServiceMutex"

// Размеры буферов
#define RC_TLS_RECORD_MAX              16384
#define RC_RELAY_BUFFER_SIZE           16384
