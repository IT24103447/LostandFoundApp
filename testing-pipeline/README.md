# ASE Testing Pipeline

## 1. Individual Contributions and Detailed Instructions

| Testing area | Student ID | Student name | Tools | Detailed README |
|---|---|---|---|---|
| Unit Testing & Mocking; CI workflow | IT24103447 | Ranasinghe A.B | xUnit, Moq, GitHub Actions | **[Unit Testing Instructions](unit-testing/README.md)** |
| Mutation Testing | IT24103457 | Karunaratne J.M.N.V.B | Stryker.NET | **[Mutation Testing Instructions](mutation-testing/README.md)** |
| Load & Performance Testing | IT24610328 | Wattegama B. C. | Apache JMeter | **[Load Testing Instructions](load-testing/README.md)** |
| UI / E2E Testing | IT24103581 | Lalanga J. A. L. | Selenium WebDriver, xUnit | **[UI/E2E Testing Instructions](ui-e2e-testing/LostFound.E2E.Tests/README.md)** |

## 2. Tool Setup

Install tools in the operating system where you will run the tests.

| Tool | Version / setup | Required for |
|---|---|---|
| .NET SDK | 8.0.424 | Unit, mutation, UI tests, and backend services |
| Node.js and npm | Node.js 22.14.0 or 24.x | Reports, frontend, and some unit tests |
| Git for Windows with Git Bash | Windows installation | Running Bash scripts and the unit helper on Windows |
| Bash and curl | Install in Ubuntu; included with Git Bash on Windows | Running scripts |
| Docker Desktop or Docker Engine with Compose | Installed; your user must be able to run `docker info` | Load and UI test environments |
| Java | 17 or newer | Load testing |
| Google Chrome | Installed | UI testing |

The runners restore or download the testing packages automatically. Selenium Manager prepares the matching Chrome driver. The first run requires internet access.

Run all commands from the **repository root**, where `test-unit.ps1` is located.

## 3. Windows Instructions

### 3.1 Prepare Git Bash

For the Bash runners, open **Git Bash** at the repository root and keep it open throughout testing.

```bash
export ASE_RUNTIME_DIR="$(cygpath -m "$(mktemp -d)")"
```

### 3.2 Start Docker and the Application

**Required only for load and UI testing. Unit and mutation testing do not require the running application or Docker containers.**

In a separate **Windows PowerShell** window:

```powershell
docker desktop start
docker info
```

If your Docker Desktop version does not support the start command, open Docker Desktop from the Start menu. Wait until `docker info` displays the server information.

Return to **Git Bash**:

```bash
export ASE_COMPOSE_PROJECT=ase-testing-local

bash testing-pipeline/shared/start-environment.sh start
source "$ASE_RUNTIME_DIR/environment.sh"
```

The script starts MySQL, Kafka, AuthService, ItemService, MatchingService, AdminVerifyService, and the frontend.

Wait for **"Shared environment ready"** before running load or UI tests.

### 3.3 Run Unit Testing

Choose either Windows method.

**PowerShell:**

```powershell
.\test-unit.ps1 -Prepare
.\test-unit.ps1 -NoBuild
```

Repeat `-Prepare` after changing the source code.

**Git Bash:**

Complete Section 3.1 first.

```bash
export ASE_REPORT_ROOT="$ASE_RUNTIME_DIR/unit-reports-$(date +%s)"
bash testing-pipeline/unit-testing/run.sh
```

### 3.4 Run Mutation Testing

Complete Section 3.1 and run the unit tests successfully first.

**Git Bash:**

```bash
export ASE_REPORT_ROOT="$ASE_RUNTIME_DIR/mutation-reports-$(date +%s)"
bash testing-pipeline/mutation-testing/run.sh
```

### 3.5 Run Load Testing

Complete Sections 3.1-3.2 first.

**Git Bash:**

```bash
bash testing-pipeline/shared/seed-test-data.sh load-testing

export ASE_REPORT_ROOT="$ASE_RUNTIME_DIR/load-reports-$(date +%s)"
bash testing-pipeline/load-testing/run.sh
```

### 3.6 Run UI/E2E Testing

Complete Sections 3.1-3.2 first.

**Git Bash:**

```bash
bash testing-pipeline/shared/seed-test-data.sh ui-e2e-testing

export ASE_REPORT_ROOT="$ASE_RUNTIME_DIR/ui-reports-$(date +%s)"
bash testing-pipeline/ui-e2e-testing/run.sh
```

### 3.7 Stop the Application

After load or UI testing, including after a failure:

**Git Bash:**

```bash
bash testing-pipeline/shared/start-environment.sh stop
```

## 4. Ubuntu Instructions

Run every command in this section in an **Ubuntu terminal** at the repository root. Keep that terminal open throughout testing.

### 4.1 Prepare the Terminal

```bash
export ASE_RUNTIME_DIR="$(mktemp -d)"
```

### 4.2 Start Docker and the Application

**Required only for load and UI testing. Unit and mutation testing do not require the running application or Docker containers.**

For Ubuntu with Docker Engine installed and Docker access configured for your user:

```bash
sudo systemctl start docker
docker info
```

For Ubuntu through WSL2, start Docker Desktop on Windows as shown in Section 3.2 and enable integration with your Ubuntu distribution. Then run `docker info` in Ubuntu. You do not need to start a separate Docker Engine inside WSL2.

Once `docker info` displays the server information, start the application:

```bash
export ASE_COMPOSE_PROJECT=ase-testing-local

bash testing-pipeline/shared/start-environment.sh start
source "$ASE_RUNTIME_DIR/environment.sh"
```

The script starts MySQL, Kafka, all four backend services, and the frontend. Wait for **"Shared environment ready"**.

### 4.3 Run Unit Testing

Complete Section 4.1 first.

```bash
export ASE_REPORT_ROOT="$ASE_RUNTIME_DIR/unit-reports-$(date +%s)"
bash testing-pipeline/unit-testing/run.sh
```

### 4.4 Run Mutation Testing

Complete Section 4.1 and run the unit tests successfully first.

```bash
export ASE_REPORT_ROOT="$ASE_RUNTIME_DIR/mutation-reports-$(date +%s)"
bash testing-pipeline/mutation-testing/run.sh
```

### 4.5 Run Load Testing

Complete Sections 4.1-4.2 first.

```bash
bash testing-pipeline/shared/seed-test-data.sh load-testing

export ASE_REPORT_ROOT="$ASE_RUNTIME_DIR/load-reports-$(date +%s)"
bash testing-pipeline/load-testing/run.sh
```

### 4.6 Run UI/E2E Testing

Complete Sections 4.1-4.2 first.

```bash
bash testing-pipeline/shared/seed-test-data.sh ui-e2e-testing

export ASE_REPORT_ROOT="$ASE_RUNTIME_DIR/ui-reports-$(date +%s)"
bash testing-pipeline/ui-e2e-testing/run.sh
```

### 4.7 Stop the Application

After load or UI testing, including after a failure:

```bash
bash testing-pipeline/shared/start-environment.sh stop
```

For either operating system:

- Run one testing type at a time, with only one application environment running.
- Repeat the report-folder command before every run.
- Before switching between load and UI testing, stop the application and repeat Sections 3.1-3.2 (Windows) or 4.1-4.2 (Ubuntu).
- Perform one warm-up load run before a demonstration.

## 5. Run the Automated Pipeline

1. Open **GitHub -> Actions -> ASE testing pipeline -> Run workflow**.
2. Select branch **`ASE/Assignment`**.
3. Select category **`all`** and mode **`strict`**.
4. Run the workflow.

To run one testing type, select `unit-testing`, `mutation-testing`, `load-testing`, or `ui-e2e-testing` instead of `all`. Mutation testing automatically runs its unit-test baseline first and proceeds only if it passes.
