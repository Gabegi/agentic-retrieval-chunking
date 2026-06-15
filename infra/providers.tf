# providers.tf
# Terraform and provider version constraints

terraform {
  required_version = ">= 1.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.0"
    }
  }

  backend "azurerm" {
    storage_account_name = "therealbestnamesa"
    container_name       = "agentic-rag-chunking"
    key                  = "terraform.tfstate"
    use_azuread_auth     = true
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id
  features {
    resource_group {
      prevent_deletion_if_contains_resources = false
    }
  }
}

resource "azurerm_resource_group" "main" {
  name     = "rg-support-agent-dev"
  location = "eastus"

  tags = {
    project     = "support-agent"
    environment = "dev"
  }
}