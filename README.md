![build](https://github.com/contract-watcher/contract-watcher-micro-db/actions/workflows/build.yml/badge.svg)
![release](https://github.com/contract-watcher/contract-watcher-micro-db/actions/workflows/release.yml/badge.svg)
[![NuGet](https://img.shields.io/nuget/v/ContractWatcher.MicroDb.svg)](https://www.nuget.org/packages/ContractWatcher.MicroDb)
[![codecov](https://codecov.io/gh/contract-watcher/contract-watcher-micro-db/branch/main/graph/badge.svg)](https://codecov.io/gh/contract-watcher/contract-watcher-micro-db)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://opensource.org/licenses/MIT)

# ContractWatcher.MicroDb

`ContractWatcher.MicroDb` — легковесная in-memory-first база данных для .NET с сохранением состояния в JSON-файлах.

Библиотека предназначена для небольших локальных хранилищ, где данные должны быть доступны напрямую из памяти во время работы приложения, но при этом сохраняться между его запусками.

Каждый `MicroDbSet` хранит текущее состояние сущностей в памяти и связан с отдельным JSON-файлом на диске. При запуске данные загружаются из файла в память, после чего операции чтения выполняются без обращения к файловой системе.

Изменения выполняются над in-memory состоянием и сохраняются на диск явно через `SaveChangesAsync()`.

### Основные возможности

- хранение сущностей в памяти;
- сохранение состояния в JSON-файлах;
- `Add`, `Update`, `Upsert`, `Remove`, `Find` и `Contains`;
- поддержка `IEnumerable<T>` и LINQ-to-Objects;
- явное сохранение через `SaveChangesAsync()`;
- конкурентное чтение и изменение состояния;
- immutable snapshots;
- безопасное сохранение через временный файл;
- поддержка `CancellationToken`;
- отслеживание несохранённых изменений через `HasChanges`.

MicroDb не предназначен для замены полноценной СУБД. Библиотека ориентирована на небольшие локальные хранилища, кеши, recovery state и другие сценарии, где необходим простой persistent storage без отдельного database server.

## Quick start

Установить пакет можно с помощью команды

```bash
dotnet add package ContractWatcher.MicroDb
```

### 1. Сущность

```csharp
public sealed record User(string Id, string Name);
```

### 2. Контекст

```csharp
using ContractWatcher.MicroDb;
using ContractWatcher.MicroDb.Options;

public sealed class AppDbContext : MicroDbContext
{
    public MicroDbSet<User, string> Users { get; }

    public AppDbContext() : base(new MicroDbOptions { DirectoryPath = "./data" })
    {
        Users = Set<User, string>("users", user => user.Id);
    }
}
```

### 3. Использование

```csharp
using var db = new AppDbContext();

db.Users.Upsert(new User("1", "Name"));
await db.SaveChangesAsync();

var user = db.Users.Find("1");
Console.WriteLine(user?.Name);
```

Данные сохраняются в `./data/users.json` и загружаются при следующем создании контекста.

> Изменения, для которых не был вызван `SaveChangesAsync()`, существуют только в памяти и будут потеряны после завершения процесса.