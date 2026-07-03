terraform {
  required_version = ">= 1.15.7"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }

  backend "azurerm" {}
}

provider "azurerm" {
  features {}
}

# Hub/connectivity subscription (cor-connectivity-prd) - owns the central
# private DNS zones our private endpoints need to resolve against
# (docs/platform-team-dns-verzoek.md). Read-only use only (data sources) -
# this repo doesn't manage anything in that subscription. Same OIDC identity
# as the default provider; it just needs at least Reader there, confirmed
# via the diagnostic step in 1-infra-deploy.yml.
provider "azurerm" {
  alias           = "hub"
  subscription_id = "c8e46005-ce0e-4be5-9ded-0178e19fbe28" # cor-connectivity-prd
  features {}
}
