# ---------------------------------------------------------------------------
# OpenAI model deployments on the existing Foundry AI Services account
# (data.azurerm_cognitive_account.foundry, see data.tf). Model choices and
# quota verified 2026-07-02 against cor-cap-dev/westeurope - see
# docs/ai-foundry-models.md. gpt-4.1 is blocked for new deployments
# (ServiceModelDeprecating); gpt-5.4 is the newest GA flagship with quota
# actually available (gpt-5.5 exists but has 0 quota in this sub/region).
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
