## requirements
- A mollie API key
- Dotnet 7.0.x
- An internet connection

## setup
Create a `appsettings.Development.json` file and overwrite the API key with your own. Also overwrite the file path to your csv file. Check donators.csv.example to see the expected format.

## run
`dotnet run --project "MollieScript.csproj" environment=Development`