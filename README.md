# SaaSApp – Modular Monolith SaaS (V6 API)

<!-- test: git push practice on branch dev-aravinth (safe to remove) -->

Production-ready ASP.NET Core Web API with Clean Architecture, CQRS (MediatR), domain events, multi-tenancy, and Azure-ready configuration.

For a detailed endpoint inventory and implementation status, see [docs/V6_API_STATUS.md](docs/V6_API_STATUS.md).

## Structure

```
/src
  /Api                          # ASP.NET Core Web API
  /BuildingBlocks
    /Catalog                     # Tenant registry (database-per-tenant)
    /SharedKernel                # Domain events, base entities, ITenantEntity
    /MultiTenancy                # ITenantProvider, tenant connection resolution
    /Logging                     # Serilog, correlation ID middleware
    /Security                    # JWT (Entra ID, Auth0, Ezofis), policies
  /Modules
    /Users                       # Users.Domain, Users.Application, Users.Infrastructure
    /Workflow                    # Workflow designer, instances, forms, AP agent
    /Repository                  # Static repository / archive file management
    /Dms                         # Document management (folders, files)
    /BlobStorage                 # Azure/local blob storage abstraction
    /Billing                     # Skeleton module
    /Reporting                   # Skeleton module
  /Workers
    /HangfireWorker              # Background job worker
/scripts                        # Database setup and E2E test scripts
/docs                           # Workflow specs, API status, payload examples
```

## Features

- **Clean Architecture** – Domain, Application, Infrastructure per module
- **CQRS** – MediatR commands/queries and pipeline behaviors (e.g. transaction)
- **Domain events** – Raised from entities, dispatched after `SaveChanges`
- **Multi-tenancy** – Catalog DB + per-tenant databases; `X-Tenant-Id` header or JWT `tid` claim
- **Auth** – Microsoft Entra ID, Auth0, and Ezofis JWT; 2FA (TOTP); policy-based roles (Admin, TenantUser)
- **Workflows** – Designer, instances, move-next/actions, SLA, connectors, AP agent integration
- **Repository & DMS** – Static repositories, archive uploads, folder/file management
- **Logging** – Serilog, structured JSON, Application Insights, correlation ID
- **Background jobs** – Hangfire with SQL Server storage
- **Security** – HTTPS redirection, secure headers, Key Vault placeholders

## Configuration

Copy `src/Api/appsettings.example.json` to `appsettings.Development.json` and set:

- **Connection string**: `ConnectionStrings:DefaultConnection` (catalog database)
- **Redis** (optional): `ConnectionStrings:Redis` — falls back to in-memory cache when empty
- **Ezofis auth**: `EzofisAuth` (SigningKey, Issuer, Audience)
- **Entra ID**: `AzureAd` (TenantId, ClientId, Audience)
- **Auth0**: `Auth0:Domain`
- **Application Insights**: `ApplicationInsights:ConnectionString`
- **Key Vault**: Configure `KeyVault:Endpoint` (see `src/Api/Configuration/KeyVaultExtensions.cs`)

## Running

1. Set connection string and auth settings in `appsettings.Development.json`.
2. Create **catalog** (tenant registry) database:
   ```powershell
   .\scripts\CreateDatabase.ps1
   ```
   This applies the catalog migration to `DefaultConnection` and creates `catalog.Tenants`.
3. For each **tenant** you can either:
   - **Signup (auto-create DB)**: `POST /api/signup` with body `{ "tenantId": "<guid>", "name": "Tenant 01", "databaseName": "SaaSApp_Tenant_01" }`. Requires CREATE DATABASE permission on the server.
   - **Manual**: create the DB, run `.\scripts\UpdateTenantDatabase.ps1`, then `POST /api/admin/tenants` (Admin).
4. Run the API:
   ```bash
   cd src/Api
   dotnet run
   ```
5. Health: `GET https://localhost:5001/health`
6. Swagger (dev): `https://localhost:5001/swagger`
7. Hangfire dashboard: `https://localhost:5001/hangfire` (add auth in production)

## Azure API Management

- Use correlation ID header `X-Correlation-ID` for tracing.
- Secure headers and CORS can be tuned at APIM or in-app as needed.
- Backend validates JWT issued by Entra ID, Auth0, or Ezofis; APIM can also validate and pass claims.

## CI

GitHub Actions builds the solution on every push and pull request to `main` (see `.github/workflows/ci.yml`).

## Users module

- **Create user**: `POST /api/users` with `{ "email": "...", "displayName": "..." }` (requires TenantUser or Admin).
- **Flow**: `CreateUserCommand` → handler creates `User` → `TransactionBehavior` saves → domain events dispatched → welcome email job enqueued.

## Billing & Reporting

Skeleton modules are in place; add entities, commands, and infrastructure following the same pattern as Users.
