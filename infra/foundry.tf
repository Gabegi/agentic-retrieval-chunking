resource "azurerm_key_vault" "foundry" {
  name                = "kv-rag-chunking-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = "standard"

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

resource "azurerm_ai_foundry" "main" {
  name                = "aif-rag-chunking-dev"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  storage_account_id  = azurerm_storage_account.documents.id
  key_vault_id        = azurerm_key_vault.foundry.id

  identity {
    type = "SystemAssigned"
  }

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

resource "azurerm_ai_foundry_project" "main" {
  name               = "proj-rag-chunking-dev"
  location           = azurerm_resource_group.main.location
  ai_services_hub_id = azurerm_ai_foundry.main.id

  identity {
    type = "SystemAssigned"
  }

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

# Allow the SP running Terraform to manage the Foundry project
resource "azurerm_role_assignment" "sp_foundry_contributor" {
  scope                = azurerm_ai_foundry_project.main.id
  role_definition_name = "Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

# Allow the ACI indexer to call models deployed in the Foundry project
resource "azurerm_role_assignment" "aci_foundry_inference" {
  scope                = azurerm_ai_foundry_project.main.id
  role_definition_name = "Azure AI Developer"
  principal_id         = azurerm_user_assigned_identity.aci_indexer.principal_id
}
