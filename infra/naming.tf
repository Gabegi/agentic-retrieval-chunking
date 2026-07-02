locals {
  # ---------------------------------------------------------------------------
  # Naming convention, deduced from existing landing zone resources. Each
  # resource builds its own name from these primitives, inline, where it's
  # defined - this file only holds the shared convention, not concrete names.
  #
  #   Resource groups : cor-cap-<workload>-<env>-<region>-<instance>
  #   Most resources  : cor-<type>-cap-<env>-<region>-<instance>
  #   Multi-target       cor-<type>-<target>-cap-<env>-<region>-<instance>
  #     resources (eg      (private endpoints: cor-pep-ais-cap-dev-we-001)
  #     private            NIC = "<private endpoint name>_nic"
  #     endpoints)
  #   Subnets         : cor-snet-cap-<purpose>-<instance>   (no env/region)
  #   Storage accounts: cor + st + <purpose> + cap + <env> + <region>
  #                     (no dashes/instance - alphanumeric, <=24 chars)
  #
  # env_short: "dev" stays "dev", but "prod" becomes "prd" in resource names
  # (see .pipelines/1-infra-deploy.yml backendRgName: cor-cap-cicd-prd-we-001).
  # ---------------------------------------------------------------------------

  region_short = {
    westeurope = "we"
  }

  env_short = {
    dev  = "dev"
    prod = "prd"
  }

  region   = local.region_short[var.location]
  env      = local.env_short[var.environment]
  instance = "001"
}
