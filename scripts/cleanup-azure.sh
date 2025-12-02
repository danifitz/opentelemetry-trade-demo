#!/bin/bash

#######################################################################
# Azure Resource Cleanup Script
# Deletes the Azure resources created for the tracing demo
#######################################################################

set -e

# Configuration
RESOURCE_GROUP="${RESOURCE_GROUP:-tracing-demo-rg}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${RED}======================================${NC}"
echo -e "${RED}Azure Resource Cleanup Script${NC}"
echo -e "${RED}======================================${NC}"
echo ""
echo -e "${YELLOW}WARNING: This will delete the following resource group and all its contents:${NC}"
echo "  Resource Group: $RESOURCE_GROUP"
echo ""

# Check if Azure CLI is installed
if ! command -v az &> /dev/null; then
    echo -e "${RED}Error: Azure CLI is not installed.${NC}"
    exit 1
fi

# Check if logged in
if ! az account show &> /dev/null; then
    echo -e "${RED}Error: Not logged in to Azure. Please run 'az login' first.${NC}"
    exit 1
fi

# Check if resource group exists
if ! az group show --name "$RESOURCE_GROUP" &> /dev/null; then
    echo -e "${YELLOW}Resource group '$RESOURCE_GROUP' does not exist. Nothing to delete.${NC}"
    exit 0
fi

# Confirm deletion
read -p "Are you sure you want to delete this resource group? (yes/no): " CONFIRM
if [ "$CONFIRM" != "yes" ]; then
    echo "Cleanup cancelled."
    exit 0
fi

# Delete resource group
echo -e "${YELLOW}Deleting resource group: $RESOURCE_GROUP...${NC}"
echo "  (This may take a few minutes...)"
az group delete \
    --name "$RESOURCE_GROUP" \
    --yes \
    --no-wait

echo -e "${GREEN}✓ Resource group deletion initiated${NC}"
echo ""
echo "The resource group is being deleted in the background."
echo "You can check the status with:"
echo "  az group show --name $RESOURCE_GROUP"
echo ""

# Remove .env file if it exists
if [ -f ".env" ]; then
    read -p "Do you want to remove the .env file? (yes/no): " REMOVE_ENV
    if [ "$REMOVE_ENV" == "yes" ]; then
        rm .env
        echo -e "${GREEN}✓ .env file removed${NC}"
    fi
fi

