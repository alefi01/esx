# Развёртывание на Windows Server 2022 + IIS

По шагам, с проверкой после каждого. Все команды — в PowerShell
**от имени администратора**.

Обозначения:
- `C:\inetpub\ftp` — куда ставим приложение
- `C:\build\portal` — куда скопированы исходники
- `ftp.domen.pro` — DNS-имя сайта (уже настроено).
  Пользователи будут заходить и по короткому имени `ftp` — под это
  на шаге 5 настраивается отдельная привязка.

---

## Шаг 1. Установить ASP.NET Core Hosting Bundle

Файл `dotnet-hosting-10.0.10-win.exe` из [02-что-скачать.md](02-что-скачать.md).

```powershell
.\dotnet-hosting-10.0.10-win.exe /install /quiet /norestart
```

**Обязательно** перезапустите IIS, иначе он не увидит новый модуль:

```powershell
net stop was /y
net start w3svc
```

Проверка — модуль должен быть в списке:

```powershell
Get-WebGlobalModule | Where-Object Name -like "*AspNetCore*"
```

Ожидается строка `AspNetCoreModuleV2`. Если её нет — Hosting Bundle не
установился или IIS не перезапущен.

> Частая ловушка: если IIS установили **после** Hosting Bundle, модуль
> не зарегистрируется. Тогда просто запустите установщик ещё раз
> с ключом `/repair`.

---

## Шаг 2. Установить SDK и собрать приложение

```powershell
.\dotnet-sdk-10.0.302-win-x64.exe /install /quiet /norestart
```

Закройте и откройте PowerShell (чтобы обновился `PATH`), затем:

```powershell
cd C:\build\portal
dotnet --version          # должно показать 10.0.302
dotnet publish src\Portal.Web -c Release -o C:\inetpub\ftp
```

Интернет не нужен: единственный пакет берётся из папки `packages\`.

Проверка — в `C:\inetpub\ftp` должны появиться:

```
Portal.Web.dll
Portal.Web.exe
web.config
appsettings.json
wwwroot\css\site.css
```

---

## Шаг 3. Создать группы в Active Directory

На контроллере домена, в оснастке «Пользователи и компьютеры», создайте
две глобальные группы безопасности:

| Группа | Кого включать |
|---|---|
| `WebUsers` | всех, кому нужен портал |
| `WebAdmins` | вас и других администраторов портала |

Членов `WebAdmins` нужно **также** добавить в `WebUsers` — либо вложить
`WebAdmins` внутрь `WebUsers` (вложенность портал понимает).

Добавьте в `WebUsers` хотя бы одну тестовую учётку и себя.

> Изменения в группах доедут до второго офиса не мгновенно — им нужна
> репликация AD. Если проверяете сразу, делайте это на том контроллере,
> где вносили правку.

---

## Шаг 4. Настроить приложение

Откройте `C:\inetpub\ftp\appsettings.json` и проверьте:

```jsonc
"ActiveDirectory": {
  "DomainFqdn": "domen.pro",
  "DomainNetBios": "DOMEN",              // ← подставьте реальное короткое имя
  "BaseDn": "DC=domen,DC=pro",
  "AccessGroup": "WebUsers",
  "AdminGroup": "WebAdmins"
}
```

Короткое имя домена можно посмотреть так:

```powershell
nltest /dsgetdc:domen.pro
```

Проверьте подсети офисов — они должны совпадать с реальными:

```jsonc
"Offices": { "Items": [
  { "Code": "office1", "Subnets": [ "192.168.96.0/20" ],  "DomainControllers": [ "192.168.96.3"  ] },
  { "Code": "office2", "Subnets": [ "192.168.112.0/20" ], "DomainControllers": [ "192.168.112.2" ] }
]}
```

`Security:RequireHttps` пока оставьте `false` — HTTPS ещё не настроен.

---

## Шаг 5. Создать пул приложений и сайт

Пул **обязательно** с `-managedRuntimeVersion ""` — приложение выполняет не
IIS, а наш собственный процесс, и загружать в пул .NET Framework не нужно.

```powershell
Import-Module WebAdministration

New-WebAppPool -Name "PortalPool"
Set-ItemProperty IIS:\AppPools\PortalPool -Name managedRuntimeVersion -Value ""
Set-ItemProperty IIS:\AppPools\PortalPool -Name startMode -Value AlwaysRunning

# Профиль пользователя нужен для работы механизма защиты данных
Set-ItemProperty IIS:\AppPools\PortalPool -Name processModel.loadUserProfile -Value $true

# Не выгружать приложение при простое: иначе первый вход утром будет долгим
Set-ItemProperty IIS:\AppPools\PortalPool -Name processModel.idleTimeout -Value "00:00:00"

# И не перезапускать его каждые 29 часов
Set-ItemProperty IIS:\AppPools\PortalPool -Name recycling.periodicRestart.time -Value "00:00:00"
```

Если сайт с заглушкой уже есть — снимите с него Basic-аутентификацию
и переключите на новую папку. Если создаёте заново:

```powershell
New-Website -Name "Portal" `
            -PhysicalPath "C:\inetpub\ftp" `
            -ApplicationPool "PortalPool" `
            -HostHeader "ftp.domen.pro" `
            -Port 80
```

### Короткое имя `ftp` — нужна вторая привязка

Пользователи будут набирать в адресной строке просто `ftp`, без домена.
Браузер при этом отправит заголовок `Host: ftp`, а привязка выше настроена
на `ftp.domen.pro` — IIS такой запрос не узнает и вернёт ошибку
«Invalid Hostname». Поэтому добавьте вторую привязку:

```powershell
New-WebBinding -Name "Portal" -Protocol http -Port 80 -HostHeader "ftp"
```

Проверить, что обе на месте:

```powershell
Get-WebBinding -Name "Portal" | Select-Object protocol, bindingInformation
```

Ожидается две строки: `*:80:ftp.domen.pro` и `*:80:ftp`.

> Короткое имя работает только у тех, у кого `domen.pro` прописан в списке
> DNS-суффиксов — у машин в домене это обычно так по умолчанию.
> Проверить на клиенте: `nslookup ftp` должен вернуть адрес веб-сервера.
>
> Если хотите вообще не думать о заголовке `Host`, можно создать привязку
> с пустым именем узла (`-HostHeader ""`) — тогда сайт отвечает на любое имя.
> Годится, пока на этом сервере один сайт; при появлении второго так делать
> уже нельзя.

**Отключите Windows-аутентификацию IIS и включите анонимную.** Это важно:
портал аутентифицирует пользователей сам, своей формой. Если оставить
встроенную аутентификацию IIS, она перехватит запрос до того, как до него
дойдёт приложение.

```powershell
Set-WebConfigurationProperty -Filter "/system.webServer/security/authentication/anonymousAuthentication" `
    -Name Enabled -Value $true -PSPath "IIS:\Sites\Portal"

Set-WebConfigurationProperty -Filter "/system.webServer/security/authentication/windowsAuthentication" `
    -Name Enabled -Value $false -PSPath "IIS:\Sites\Portal"

Set-WebConfigurationProperty -Filter "/system.webServer/security/authentication/basicAuthentication" `
    -Name Enabled -Value $false -PSPath "IIS:\Sites\Portal"
```

---

## Шаг 6. Права на папки

Приложению нужно писать только в `App_Data` — там лежат ключи, которыми
подписываются cookie.

```powershell
$pool = "IIS AppPool\PortalPool"

# Чтение и выполнение — на всю папку приложения
icacls "C:\inetpub\ftp" /grant "${pool}:(OI)(CI)(RX)" /T

# Запись — только в App_Data
New-Item -ItemType Directory -Force -Path "C:\inetpub\ftp\App_Data"
icacls "C:\inetpub\ftp\App_Data" /grant "${pool}:(OI)(CI)(M)"
```

> Содержимое `App_Data\keys` — секрет. Кто может прочитать эти файлы,
> тот может подделать cookie любого пользователя портала.
> В резервные копии — да, в общий доступ — нет.

---

## Шаг 7. Запуск и первая проверка

```powershell
Start-WebAppPool -Name "PortalPool"
Start-Website -Name "Portal"

# Проверка живости — должно вернуть "ok"
Invoke-WebRequest http://ftp.domen.pro/healthz -UseBasicParsing | Select-Object -Expand Content
```

Затем откройте `http://ftp.domen.pro/` в браузере — должна появиться форма входа.

Полный сценарий проверки: [04-проверка-этапа-1.md](04-проверка-этапа-1.md).

---

## Обновление после доработок

```powershell
Stop-WebAppPool -Name "PortalPool"

cd C:\build\portal
git pull                     # или скопируйте новые исходники вручную
dotnet publish src\Portal.Web -c Release -o C:\inetpub\ftp

Start-WebAppPool -Name "PortalPool"
```

`App_Data` при этом не затирается — ключи сохранятся, и пользователей
не выкинет из системы.

---

## Если что-то не работает

### Ошибка 500.19

Испорчен `web.config` или не установлен Hosting Bundle. Проверьте шаг 1.

### Ошибка 500.30 — приложение не стартует

Включите журнал в `C:\inetpub\ftp\web.config`:

```xml
<aspNetCore ... stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout">
```

Создайте папку и дайте пулу право записи:

```powershell
New-Item -ItemType Directory -Force -Path "C:\inetpub\ftp\logs"
icacls "C:\inetpub\ftp\logs" /grant "IIS AppPool\PortalPool:(OI)(CI)(M)"
```

Перезапустите пул, повторите запрос и посмотрите файл в `logs\`.
Обычные причины: опечатка в `appsettings.json` (например, лишняя запятая),
нет прав на `App_Data`.

**Не забудьте вернуть `stdoutLogEnabled="false"`** — иначе файлы журнала
будут расти бесконечно.

Ещё один источник сведений — журнал Windows:

```powershell
Get-EventLog -LogName Application -Source "IIS AspNetCore Module V2" -Newest 5 | Format-List
```

### Форма входа открывается, но пишет «Контроллер домена недоступен»

Проверьте связь с портов веб-сервера:

```powershell
Test-NetConnection 192.168.96.3  -Port 389
Test-NetConnection 192.168.112.2 -Port 389
```

Если порт закрыт — дело в межсетевом экране, а не в портале.
После входа администратором те же проверки показывает страница
`/Admin/Diagnostics`.

### Браузер спрашивает логин и пароль окном Windows, а не формой

На сайте осталась включённой встроенная аутентификация IIS. Вернитесь к шагу 5.
