variable "environment" {
  type        = string
  description = "Environment name (development, production) - matches 1-infra-deploy.yml's envName. See naming.tf's env_short for the separate dev/prd shorthand baked into resource names."
}

variable "location" {
  type        = string
  description = "Azure region"
}

variable "project" {
  type        = string
  description = "Project name used in resource naming"
}

variable "tags" {
  type        = map(string)
  description = "Tags applied to all resources"
  default     = {}
}

variable "openai_embedding_deployment" {
  type        = string
  description = "Deployment name for the text-embedding-3-large model on the Foundry AI Services account"
  default     = "embedding-3-large"
}

variable "openai_gpt_deployment" {
  type        = string
  description = "Deployment name for the gpt-4.1 model used by the query API"
  default     = "gpt-4.1-query"
}

variable "openai_extraction_deployment" {
  type        = string
  description = "Deployment name for the gpt-4.1 model used by the indexing/extraction pipeline"
  default     = "gpt-4.1-extraction"
}

variable "openai_eval_deployment" {
  type        = string
  description = "Deployment name for the gpt-4o model used for evaluation"
  default     = "gpt-4o-eval"
}

variable "openai_gpt_model_name" {
  type        = string
  description = "Human-readable model family name for the query/extraction GPT deployment (distinct from the deployment name)"
  default     = "gpt-5.4"
}

variable "search_index_name" {
  type        = string
  description = "Name of the Azure AI Search index used by the indexing/query pipeline"
  default     = "protocols-index"
}

variable "knowledge_source_name" {
  type        = string
  description = "Name of the Azure AI Search knowledge source"
  default     = "protocols-knowledge-source"
}

variable "knowledge_base_name" {
  type        = string
  description = "Name of the Azure AI Search knowledge base"
  default     = "protocols-knowledge-base"
}
