# Lost & Found App - Initial README

## Branch Strategy
- `main` - protected, production, requires PR + review
- `develop` - active integration branch
- `feature/*` - individual work, PR into `develop`

## Services
- `services/auth-service` - Authentication & role-based access

## Current Local Setup (Auth Service)
1. Install .NET 8 SDK
2. `cd services/auth-service/AuthService`
3. Set up `appsettings.Development.json` with your own local values (not committed, ignored):
   - `ConnectionStrings:MySql`
   - `Jwt:Secret`
   - `Kafka:BootstrapServers` (use `localhost:9092` for local Kafka)
4. Run local Kafka: `cd infra/kafka && docker compose up -d`
5. `dotnet run`

## Environment Variables (Azure App Service)
- `ConnectionStrings__MySql`
- `Jwt__Secret`
- `Kafka__BootstrapServers`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`

## CI/CD
GitHub Actions, path-filtered per service. Build + test on push to `develop`, deploy to Azure App Service on merge to `main`. `main` protected, does not allow force pushes or pushes without making a pull request. Pull request has to be approved by atleast one other memeber before push. 