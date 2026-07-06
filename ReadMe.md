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
│   ├── 0-build-test.yml        # .NET build + unit tests (runs on PR and push)
│   └── 1-infra-deploy.yml      # Terraform CI/CD (plan on PR, apply on push)
└── ReadMe.md
```


## Terraform Pipeline Configuration

| | dev | prod |
|---|---|---|
| serviceConnection | `cor-cap-app-dev` | `cor-cap-app-prd` |
| backendRgName | `cor-cap-cicd-dev-we-001` | `cor-cap-cicd-prd-we-001` |
| backendStorageAccount | `corsttfcapdevwe` | `corsttfcapprdwe` |
| backendContainer | `terraform-state` | `terraform-state` |