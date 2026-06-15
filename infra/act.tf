resource "azurerm_container_registry" "main" {
  name                = "chuningtestacr"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Basic"
  admin_enabled       = false # Use managed identity, not admin credentials

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

# Allow runner VM to push images to ACR
resource "azurerm_role_assignment" "runner_acr_push" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPush"
  principal_id         = data.azurerm_client_config.current.object_id
}

# Allow runner VM to upload scraped PDFs to blob storage
resource "azurerm_role_assignment" "runner_blob_contributor" {
  scope                = azurerm_storage_account.documents.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}