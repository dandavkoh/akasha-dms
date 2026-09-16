
# Akasha DMS

> Modern Document Management System with a .NET backend and a Vite/Node.js frontend.

Akasha DMS ist ein webbasiertes Dokumentenmanagementsystem mit einer getrennten **.NET-Backend-Anwendung** und einem **Vite-basierten Frontend**.

----------

## 📁 Projektstruktur

```text
akasha-dms/
├── backend/
│   ├── Akasha.slnx
│   ├── src/
│   │   ├── Akasha.Api/
│   │   ├── Akasha.Application/
│   │   ├── Akasha.Domain/
│   │   └── Akasha.Infrastructure/
│   └── tests/
│       └── Akasha.ArchitectureTests/
│
├── frontend/
│   ├── app/
│   │   ├── package.json
│   │   └── ...
│   │
│   └── demo/
│       ├── package.json
│       └── ...
│
└── README.md

```

----------

# 🛠️ Voraussetzungen

Für die Entwicklung werden folgende Technologien benötigt:

| Technologie | Version |
|--|--|
| Node.js | `24.13.0` |
| npm | `11.6.2` |
| .NET SDK | `10.0.401` |

Versionen überprüfen:

```bash
node --version
npm --version
dotnet --version

```

----------

# 🍎 Installation unter macOS

## Node.js

Node.js wird über **NVM (Node Version Manager)** verwaltet.

Falls NVM bereits installiert ist:

```bash
nvm use 24

```

Version überprüfen:

```bash
node --version
npm --version

```

Erwartete Versionen:

```text
v24.13.0
11.6.2

```

Falls Node.js 24 noch nicht installiert ist:

```bash
nvm install 24
nvm use 24

```

Optional kann Node.js 24 als Standardversion festgelegt werden:

```bash
nvm alias default 24

```

----------

## .NET SDK

Das .NET SDK kann unter macOS beispielsweise über Homebrew installiert werden:

```bash
brew install --cask dotnet-sdk

```

Anschließend überprüfen:

```bash
dotnet --version

```

Beispiel:

```text
10.0.401

```

----------

# 🚀 Projekt einrichten

Repository bzw. Projekt öffnen:

```bash
cd akasha-dms

```

Danach werden Backend und Frontend separat eingerichtet.

----------

# 🔵 Backend

Das Backend befindet sich im Verzeichnis:

```text
backend/

```

## Abhängigkeiten installieren

In das Backend wechseln:

```bash
cd backend

```

NuGet-Abhängigkeiten wiederherstellen:

```bash
dotnet restore

```

`dotnet restore` liest die `.csproj`-Dateien und lädt alle dort definierten NuGet-Pakete.

Build und Tests:

```bash
dotnet build
dotnet test

```

----------

## Backend starten

```bash
dotnet run --project src/Akasha.Api

```

Beim Start werden fehlende Abhängigkeiten normalerweise automatisch wiederhergestellt.

Für eine explizite Einrichtung empfiehlt sich trotzdem:

```bash
dotnet restore
dotnet run --project src/Akasha.Api

```

Unter `http://localhost:5257/health` steht ein Health-Endpoint zur Verfügung.

----------

----------

# 📦 .NET / NuGet Paketverwaltung

.NET verwendet **NuGet** als Paketverwaltung.

Die Abhängigkeiten eines Projekts werden normalerweise in der `.csproj`-Datei definiert.

Beispiel:

```xml
<ItemGroup>
    <PackageReference Include="Swashbuckle.AspNetCore" Version="6.5.0" />
</ItemGroup>

```

## Paket installieren

Ein Paket hinzufügen:

```bash
dotnet add package PaketName

```

Beispiel:

```bash
dotnet add package Newtonsoft.Json

```

Eine bestimmte Version installieren:

```bash
dotnet add package Newtonsoft.Json --version 13.0.3

```

Danach werden die Projektdateien entsprechend aktualisiert.

----------

## Paket aktualisieren

Ein bestimmtes Paket aktualisieren:

```bash
dotnet add package PaketName

```

Beispiel:

```bash
dotnet add package Newtonsoft.Json

```

Damit wird die Paketversion auf eine passende aktuelle Version gesetzt.

Alternativ kann die Version explizit angegeben werden:

```bash
dotnet add package Newtonsoft.Json --version 13.0.4

```

### Veraltete Pakete anzeigen

```bash
dotnet list package --outdated

```

Damit lässt sich überprüfen, welche NuGet-Pakete aktualisiert werden können.

----------

## Paket löschen

Ein Paket aus dem Projekt entfernen:

```bash
dotnet remove package PaketName

```

Beispiel:

```bash
dotnet remove package Newtonsoft.Json

```

----------

## Installierte Pakete anzeigen

```bash
dotnet list package

```

----------

## Abhängigkeiten wiederherstellen

Falls Pakete fehlen:

```bash
dotnet restore

```

----------

# 🟢 Frontend

Das Frontend befindet sich in:

```text
frontend/app/

```

In das Verzeichnis wechseln:

```bash
cd frontend/app

```

## Abhängigkeiten installieren

```bash
npm install

```

Dadurch werden die in `package.json` definierten Pakete installiert.

Dabei wird normalerweise auch die Datei

```text
package-lock.json

```

verwendet bzw. aktualisiert.

----------

## Frontend starten

```bash
npm run dev

```

Vite startet anschließend den Development Server.

Standardmäßig ist das Frontend unter:

```text
http://localhost:5173/

```

erreichbar.

----------

# 📦 npm Paketverwaltung

Das Frontend verwendet **npm** zur Verwaltung der JavaScript-/Node.js-Abhängigkeiten.

Die Abhängigkeiten werden in

```text
package.json

```

definiert.

Beispiel:

```json
{
  "dependencies": {
    "react": "^19.0.0"
  }
}

```

----------

## Paket installieren

Ein Paket installieren:

```bash
npm install PaketName

```

Beispiel:

```bash
npm install axios

```

Das Paket wird anschließend in `package.json` eingetragen.

----------

## Entwicklungsabhängigkeit installieren

Pakete, die nur für die Entwicklung benötigt werden, sollten mit `-D` installiert werden:

```bash
npm install PaketName --save-dev

```

Kurzform:

```bash
npm i PaketName -D

```

Beispiel:

```bash
npm install eslint -D

```

----------

## Bestimmte Version installieren

```bash
npm install PaketName@Version

```

Beispiel:

```bash
npm install axios@1.7.9

```

----------

## Paket aktualisieren

Ein einzelnes Paket aktualisieren:

```bash
npm update PaketName

```

Beispiel:

```bash
npm update axios

```

Alle Pakete aktualisieren, soweit es die in `package.json` angegebenen Versionsbereiche erlauben:

```bash
npm update

```

----------

## Veraltete Pakete anzeigen

```bash
npm outdated

```

Damit erhält man eine Übersicht über Pakete, für die neuere Versionen verfügbar sind.

Beispiel:

```text
Package    Current   Wanted   Latest
axios      1.7.9     1.7.9    1.8.0

```

Dabei bedeutet:

-   **Current** – aktuell installierte Version
    
-   **Wanted** – höchste Version innerhalb des erlaubten Versionsbereichs
    
-   **Latest** – aktuell veröffentlichte Version
    

----------

## Paket auf die neueste Version aktualisieren

Wenn unabhängig vom aktuell erlaubten Versionsbereich die neueste Version installiert werden soll:

```bash
npm install PaketName@latest

```

Beispiel:

```bash
npm install axios@latest

```

> ⚠️ Bei Major-Versionen können Breaking Changes auftreten. Nach solchen Updates sollte die Anwendung getestet werden.

----------

## Paket löschen

Ein Paket entfernen:

```bash
npm uninstall PaketName

```

Beispiel:

```bash
npm uninstall axios

```

Das Paket wird aus `node_modules` entfernt und aus `package.json` gelöscht.

----------

## Alle Abhängigkeiten neu installieren

Wenn `node_modules` beschädigt ist oder eine saubere Installation benötigt wird:

```bash
rm -rf node_modules
npm install

```

Bei einem vorhandenen `package-lock.json` kann alternativ eine reproduzierbare Installation verwendet werden:

```bash
npm ci

```

`npm ci` eignet sich besonders für CI/CD-Umgebungen.

----------

# 🔒 npm Security / Audits

Sicherheitsprobleme der installierten Pakete anzeigen:

```bash
npm audit

```

Automatisch behebbare Probleme reparieren:

```bash
npm audit fix

```

> ⚠️ `npm audit fix --force` sollte nur mit Vorsicht verwendet werden. Dabei können Breaking Changes und inkompatible Paketversionen installiert werden.

```bash
npm audit fix --force

```

----------

# 🔄 Typischer Development-Workflow

## Backend

Terminal 1:

```bash
cd akasha-dms/backend

dotnet restore
dotnet build
dotnet test
dotnet run --project src/Akasha.Api
```

## Frontend

Terminal 2:

```bash
cd akasha-dms/frontend/app

npm install
npm run dev

```

Anschließend das Frontend öffnen:

```text
http://localhost:5173/

```

----------

# 🧰 Wichtige Befehle – Übersicht

## .NET

| Aufgabe | Befehl |
|--|--|
| Version anzeigen | `dotnet --version` |
| Abhängigkeiten installieren | `dotnet restore` |
| Paket installieren | `dotnet add package NAME` |
| Bestimmte Version installieren | `dotnet add package NAME --version VERSION` |
| Pakete anzeigen | `dotnet list package` |
| Veraltete Pakete anzeigen | `dotnet list package --outdated` |
| Paket entfernen | `dotnet remove package NAME` |
| Backend starten | `dotnet run --project src/Akasha.Api` |

## npm

| Aufgabe | Befehl |
|--|--|
| Version anzeigen | `npm --version` |
| Abhängigkeiten installieren | `npm install` |
| Paket installieren | `npm install NAME` |
| Dev-Paket installieren | `npm install NAME -D` |
| Paket aktualisieren | `npm update NAME` |
| Alle Pakete aktualisieren | `npm update` |
| Veraltete Pakete anzeigen | `npm outdated` |
| Neueste Version installieren | `npm install NAME@latest` |
| Paket entfernen | `npm uninstall NAME` |
| Security Audit | `npm audit` |
| Security Fix | `npm audit fix` |
| Saubere Installation | `npm ci` |
| Development Server | `npm run dev` |

----------

# 🧹 Clean & Reinstall

## Frontend

Bei Problemen mit npm-Abhängigkeiten:

```bash
cd frontend/app

rm -rf node_modules
npm install

```

Oder mit exakt dem Lockfile:

```bash
rm -rf node_modules
npm ci

```

## Backend

Bei Problemen mit Build-Artefakten:

```bash
cd backend

dotnet clean
dotnet restore
dotnet build

```

Danach:

```bash
dotnet run --project src/Akasha.Api

```

----------

# ⚠️ Hinweise

### `package.json` und `package-lock.json`

Beide Dateien sollten grundsätzlich im Repository bleiben.

-   `package.json` beschreibt die Projektabhängigkeiten.
    
-   `package-lock.json` hält die tatsächlich aufgelösten Versionen fest.
    

`node_modules/` sollte dagegen normalerweise **nicht** in Git eingecheckt werden.

### .NET Projektdateien

Die NuGet-Abhängigkeiten werden über die `.csproj`-Dateien verwaltet.

Die erzeugten Build-Verzeichnisse wie

```text
bin/
obj/

```

sollten normalerweise ebenfalls nicht in Git eingecheckt werden.

----------

# 📌 Entwicklungsstatus

🚧 **In Development**

Akasha DMS befindet sich aktuell in der Entwicklung. Architektur, Features und Abhängigkeiten können sich noch ändern.

----------

# 📄 Lizenz

Die Lizenz des Projekts wird noch festgelegt.

----------

Made with ❤️ for **Akasha DMS**
