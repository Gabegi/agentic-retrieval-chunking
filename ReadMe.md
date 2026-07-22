# Amazing Repo

## Repository Structure
test

```
/
├── infra/                      # Terraform infrastructure
│   ├── envs/
│   │   ├── dev.tfvars
│   │   └── prod.tfvars
│   ├── main.tf
│   ├── outputs.tf
│   ├── providers.tf
│   └── variables.tf
├── src/                        # .NET application code
├── .pipelines/                 # Azure DevOps pipelines
│   ├── 3-build-test.yml        # .NET build + unit tests (runs on PR and push)
│   └── 1-infra-deploy.yml      # Terraform CI/CD (plan on PR, apply on push)
└── ReadMe.md
```


## Post-Deployment Steps

- **Run one `force=true` reindex after deploying the rolling-snapshot feature.** The snapshot only accumulates chunks touched by normal runs, so a document indexed before this feature existed (and never updated since) won't appear in it otherwise. Until that first full run, vector-cache eviction may delete still-live vectors it can't yet see in a snapshot — safe, just an avoidable re-embed later, not a correctness issue.

## Terraform Pipeline Configuration

| | dev | prod |
|---|---|---|
| serviceConnection | `cor-cap-app-dev` | `cor-cap-app-prd` |
| backendRgName | `cor-cap-cicd-dev-we-001` | `cor-cap-cicd-prd-we-001` |
| backendStorageAccount | `corsttfcapdevwe` | `corsttfcapprdwe` |
| backendContainer | `terraform-state` | `terraform-state` |

## Debug Func app

- Add IP to Function App and to Storage account
```
az functionapp config access-restriction add --resource-group cor-cap-data-dev-we-001 --name cor-func-idx-cap-dev-we-001 --rule-name AllowMyIP --action Allow --ip-address 62.194.97.137/32 --priority 100 && az functionapp config access-restriction add --resource-group cor-cap-data-dev-we-001 --name cor-func-idx-cap-dev-we-001 --rule-name AllowMyIP-SCM --action Allow --ip-address 62.194.97.137/32 --priority 100 --scm-site true
```
-  https://cor-func-idx-cap-dev-we-001.scm.azurewebsites.net
- Debug console → CMD
- Browse to D:\home\LogFiles\Application\Functions\Host\ and open the most recent .log file there — paste me what's in it (or at least the last exception/error block)

