# ---------------------------------------------------------------------------
# OpenAI model deployments on the existing Foundry AI Services account
# (data.azurerm_cognitive_account.foundry, see data.tf). Model choices and
# quota verified 2026-07-02 against cor-cap-dev/westeurope - see
# docs/ai-foundry-models.md. gpt-4.1 is blocked for new deployments
# (ServiceModelDeprecating); gpt-5.4 is the newest GA flagship with quota
# actually available (gpt-5.5 exists but has 0 quota in this sub/region).
# ---------------------------------------------------------------------------

# Looped via for_each rather than one resource block each - only the model
# and capacity vary per deployment. Renaming the old per-deployment resources
# (azurerm_cognitive_deployment.embedding/querying/extraction/evaluation) to
# openai[...] below is a pure Terraform-address move (name/model/sku
# unchanged per entry) - the moved blocks make it a no-op against the real
# deployments.

moved {
  from = azurerm_cognitive_deployment.embedding
  to   = azurerm_cognitive_deployment.openai["embedding"]
}

moved {
  from = azurerm_cognitive_deployment.querying
  to   = azurerm_cognitive_deployment.openai["querying"]
}

moved {
  from = azurerm_cognitive_deployment.extraction
  to   = azurerm_cognitive_deployment.openai["extraction"]
}

moved {
  from = azurerm_cognitive_deployment.evaluation
  to   = azurerm_cognitive_deployment.openai["evaluation"]
}

locals {
  openai_deployments = {
    embedding = {
      name          = var.openai_embedding_deployment
      model_name    = "text-embedding-3-large"
      model_version = "1"
      capacity      = 350
    }
    querying = {
      name          = var.openai_gpt_deployment
      model_name    = "gpt-5.4"
      model_version = "2026-03-05"
      capacity      = 10
    }
    extraction = {
      name          = var.openai_extraction_deployment
      model_name    = "gpt-5.4"
      model_version = "2026-03-05"
      capacity      = 40
    }
    # Deliberately a different model/version from "querying"/"extraction"
    # (gpt-5.4) to avoid self-preference bias in eval scores.
    evaluation = {
      name          = var.openai_eval_deployment
      model_name    = "gpt-5.1"
      model_version = "2025-11-13"
      capacity      = 10
    }
  }
}

resource "azurerm_cognitive_deployment" "openai" {
  for_each             = local.openai_deployments
  name                 = each.value.name
  cognitive_account_id = data.azurerm_cognitive_account.foundry.id

  model {
    format  = "OpenAI"
    name    = each.value.model_name
    version = each.value.model_version
  }

  sku {
    name     = "GlobalStandard"
    capacity = each.value.capacity
  }
}
