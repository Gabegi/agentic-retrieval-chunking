resource "azurerm_cognitive_account" "openai" {
  name                  = "oai-chuking-agentic-rag"
  resource_group_name   = azurerm_resource_group.main.name
  location              = azurerm_resource_group.main.location
  kind                  = "OpenAI"
  sku_name              = "S0"
  custom_subdomain_name = "oai-chuking-agentic-rag"

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

resource "azurerm_cognitive_deployment" "embedding" {
  name                 = var.openai_embedding_deployment
  cognitive_account_id = azurerm_cognitive_account.openai.id

  model {
    format  = "OpenAI"
    name    = "text-embedding-3-large"
    version = "1"
  }

  sku {
    name     = "Standard"
    capacity = 350
  }
}

resource "azurerm_cognitive_deployment" "querying" {
  name                 = var.openai_gpt_deployment
  cognitive_account_id = azurerm_cognitive_account.openai.id

  model {
    format  = "OpenAI"
    name    = "gpt-4.1"
    version = "2025-04-14"
  }

  sku {
    name     = "Standard"
    capacity = 10
  }
}

resource "azurerm_cognitive_deployment" "extraction" {
  name                 = var.openai_extraction_deployment
  cognitive_account_id = azurerm_cognitive_account.openai.id

  model {
    format  = "OpenAI"
    name    = "gpt-4.1"
    version = "2025-04-14"
  }

  sku {
    name     = "Standard"
    capacity = 40
  }
}


resource "azurerm_role_assignment" "sp_openai_user" {
  scope                = azurerm_cognitive_account.openai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = data.azurerm_client_config.current.object_id
}