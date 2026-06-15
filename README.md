# SQL Best Practice Assessment Aggregator

A .NET 10 web app that aggregates SQL Server BPA results across Azure Arc-enabled and Azure VM SQL Servers. Pulls data from two Log Analytics workspaces using the App Service's managed identity.

## Deployment

Prerequisites
Azure App Service (Linux, .NET 10) with system-assigned managed identity
Two Log Analytics workspaces with SqlAssessment_CL data
Log Analytics Reader role for the MI on each workspace

Disclaimer
Demo environment — not intended for production use.