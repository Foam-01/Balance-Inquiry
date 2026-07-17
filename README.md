# 📦 Balance Inquiry Data Pipeline

A full-stack enterprise data pipeline designed to automate the retrieval of customer portfolio balances and account information. The system extracts Type-7 customer accounts from the **SBA Informix Database**, retrieves investment balances from the **FundConnext API**, processes the data, and generates standardized daily reports for downstream fund management systems.

---

# 🔧 Tech Stack

## 💻 Backend

- **Framework:** .NET 10 (C#)
- **Web Server:** ASP.NET Core Minimal API (Kestrel)
- **Configuration:** App.config
- **Scheduler:** Background Service
- **HTTP Client:** HttpClient
- **Serialization:** System.Text.Json

## 🗄️ Data Sources

- **Database:** SBA Informix Database (ODBC x86)
- **External API:** FundConnext API

## 🔄 Data Integration

- **Child Process Communication**
- **JSON File Exchange**
- **UTF-8 File Export**
- **Pipe Delimited Reports**

---

# 📋 Key Features

## 1. Automated Balance Inquiry Pipeline

The system automatically retrieves customer account information from the SBA database, requests portfolio balances from FundConnext, and generates standardized output files for downstream systems.

Features

- Automated customer retrieval
- Portfolio balance synchronization
- Daily report generation
- Scheduled execution
- Manual API triggering

---

## 2. Isolated SBA Export Process

To ensure compatibility between legacy Informix drivers and modern .NET applications, the SBA extraction process runs as a dedicated **32-bit child process**.

Features

- 32-bit Informix ODBC support
- Memory isolation
- Automatic process termination
- JSON-based communication
- Connection leak prevention

---

## 3. FundConnext Integration

The pipeline authenticates with FundConnext and retrieves customer portfolio balances using secure HTTP APIs.

Features

- Access Token Management
- Token Caching
- Portfolio Balance Retrieval
- Automatic Retry
- HTTP Client Service

---

## 4. Daily Report Generator

Generates standardized UTF-8 encoded report files for downstream processing.

Generated Files

- **FSTKH_yyyyMMdd.txt** (Customer Header)
- **FSTKD_yyyyMMdd.txt** (Portfolio Detail)

---

## 5. Background Scheduler

Runs automatically based on predefined business schedules.

Features

- Monthly Scheduler
- First Business Day Detection
- Automatic Pipeline Execution
- Manual Trigger Support

---

# 🏗️ System Architecture

```text
                 +------------------------------------+
                 | FundBalanceDataPipeline.dll        |
                 | ASP.NET Core / AnyCPU (64-bit)     |
                 +----------------+-------------------+
                                  |
                    Launch Child Process
                                  |
                                  v
                 +------------------------------------+
                 | SbaExporter.exe                    |
                 | Console Application (x86)          |
                 +----------------+-------------------+
                                  |
                          Informix ODBC Driver
                                  |
                                  v
                       SBA Informix Database
                                  |
                           Export Customer Data
                                  |
                                  v
                      sba_temp_data.json
                                  |
                 +----------------+-------------------+
                 |
          Read Temporary JSON
                 |
        Retrieve Portfolio Balances
                 |
                 v
          FundConnext API
                 |
                 v
      Generate UTF-8 Report Files
                 |
                 v
      FSTKH_xxxxx.txt
      FSTKD_xxxxx.txt
```

---

# 📁 Project Structure

```text
Balance-Inquiry/
├── README.md                                          # Project documentation and architecture overview
│
├── FundBalanceDataPipeline/                           # Main pipeline service (AnyCPU / 64-bit)
│   ├── Program.cs                                     # Application entry point and API host
│   ├── App.config                                     # Application configuration
│   ├── Models/
│   │   └── CustomerPipelineData.cs                    # Customer data model
│   ├── Infrastructure/
│   │   ├── FundConnextAuthService.cs                  # Authentication service
│   │   └── FundConnextClient.cs                       # FundConnext API client
│   ├── Services/
│   │   └── AutoPipelineScheduler.cs                   # Background scheduler
│   └── FundBalanceDataPipeline.csproj
│
└── SbaExporter/                                       # Legacy SBA Exporter (x86)
    ├── Program.cs                                     # Console application entry point
    ├── SbaDatabaseService.cs                          # Informix database service
    ├── SbaExporter.csproj                             # x86 project configuration
    └── App.config
```

---

# 📄 Report Specifications

## FSTKH_yyyyMMdd.txt

Customer Header Report

Contains

- Customer Account
- Customer Name
- Branch
- Address
- Investment Advisor
- Tax ID

Encoding

- UTF-8
- Pipe Delimited

---

## FSTKD_yyyyMMdd.txt

Portfolio Detail Report

Contains

- Account Number
- Fund Company
- Fund Code
- Units
- NAV Date
- Average Cost
- NAV Price
- Cost Amount
- Market Value
- Unrealized Gain/Loss

Encoding

- UTF-8
- Pipe Delimited

---

# 🚀 Getting Started

## Run Pipeline

```bash
cd FundBalanceDataPipeline

dotnet bin\Debug\net10.0\FundBalanceDataPipeline.dll
```

> **Note**
>
> Use `dotnet.exe` instead of `dotnet run` to avoid enterprise endpoint security software (such as CrowdStrike or SentinelOne) terminating long-running child processes.

---

# 📡 API Endpoints

## Run Pipeline

```http
POST /api/pipeline/run
```

Run the entire balance inquiry pipeline.

---

## Run Selected Accounts

```http
POST /api/pipeline/run?accountNo=0061117-7,0058618-7
```

Execute the pipeline for specific customer accounts.

---

## Pipeline Status

```http
GET /api/pipeline/status
```

Retrieve the current processing status.

---

# 👨‍💻 Developer

Developed with ❤️ by **Sitthidet Thongsawang**
