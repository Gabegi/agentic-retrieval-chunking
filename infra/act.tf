data "azurerm_virtual_machine" "runner" {
  name                = "vm-github-runner"
  resource_group_name = "rg-github-runner"
}

resource "azurerm_container_registry" "main" {
  name                = "crragtest"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Basic"
  admin_enabled       = false # Use managed identity, not admin credentials

  tags = {
    project     = "support-agent"
    environment = "dev"
  }
}

# Allow runner VM to push images to ACR
resource "azurerm_role_assignment" "runner_acr_push" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPush"
  principal_id         = data.azurerm_virtual_machine.runner.identity[0].principal_id
}