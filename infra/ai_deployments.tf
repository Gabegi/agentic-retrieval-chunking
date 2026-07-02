# ---------------------------------------------------------------------------
# OpenAI model deployments on the existing Foundry AI Services account
# (data.azurerm_ai_services.foundry, see data.tf). Capacities are copied
# from a prior prototype's quota-fitted values - verify TPM quota for this
# subscription (cor-cap-dev / westeurope) before relying on them.
# ---------------------------------------------------------------------------

resource "azurerm_cognitive_deployment" "embedding" {
  name                 = var.openai_embedding_deployment
  cognitive_account_id = data.azurerm_cognitive_account.foundry.id

  model {
    format  = "OpenAI"
    name    = "text-embedding-3-large"
    version = "1"
  }

  sku {
    name     = "GlobalStandard"
    capacity = 350
  }
}


resource "azurerm_cognitive_deployment" "querying" {
  name                 = var.openai_gpt_deployment
  cognitive_account_id = data.azurerm_cognitive_account.foundry.id

  model {
    format  = "OpenAI"
    name    = "gpt-4.1"
    version = "2025-04-14"
  }

  sku {
    name     = "GlobalStandard"
    capacity = 10
  }
}

resource "azurerm_cognitive_deployment" "extraction" {
  name                 = var.openai_extraction_deployment
  cognitive_account_id = data.azurerm_cognitive_account.foundry.id

  model {
    format  = "OpenAI"
    name    = "gpt-4.1"
    version = "2025-04-14"
  }

  sku {
    name     = "GlobalStandard"
    capacity = 40
  }
}

resource "azurerm_cognitive_deployment" "evaluation" {
  name                 = var.openai_eval_deployment
  cognitive_account_id = data.azurerm_cognitive_account.foundry.id

  model {
    format  = "OpenAI"
    name    = "gpt-4o"
    version = "2024-11-20"
  }

  sku {
    name     = "GlobalStandard"
    capacity = 10
  }
}
