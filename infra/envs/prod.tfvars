environment = "production"
location    = "westeurope"
project     = "cor-cap-app"
# Guessed from the env_short convention (naming.tf: development -> dev, production -> prd) -
# the project itself is platform-provisioned and doesn't follow this repo's
# naming convention (see data.tf), so this is unverified. Confirm the actual
# name with the platform team before the first real prod apply.
foundry_project_name = "cor-cap-dvt-prd"

tags = {
  owner = "platform"
}
