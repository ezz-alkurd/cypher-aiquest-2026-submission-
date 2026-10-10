# Cypher — AI Quest 2026

Cypher helps small businesses manage products, stock, orders, and customer conversations from one owner dashboard. A business can use the inventory tools alone or enable a customer storefront. The website assistant answers questions using that business's current catalog; the WhatsApp workflows can turn confirmed conversations into orders for owner review.

## Review the live demo

| Step | Link or action |
|---|---|
| Open the platform | [Cypher](https://ezzeldeenalkurd-001-site2.ftempurl.com/) — explore services and the join request |
| Shop as a customer | [Demo storefront](https://ezzeldeenalkurd-001-site2.ftempurl.com/s/shadi-test) — browse products, add several items, then check out with test contact details |
| Ask the AI | Use the assistant button on the demo storefront to ask about products, prices, or availability |
| Order through WhatsApp | [Chat with Shadi's demo store](https://wa.me/962797815644) (`0797815644`) — ask about a product, choose a quantity, and explicitly confirm the order |
| Manage the business | [Owner sign-in](https://ezzeldeenalkurd-001-site2.ftempurl.com/admin/login) — username `shadi-test`, password `ShadiDemo!2026` |
| Inspect the order | In the [owner dashboard](https://ezzeldeenalkurd-001-site2.ftempurl.com/admin), open Orders to review website or WhatsApp requests and update their status; inspect Products, Inventory, Categories, Statistics, and Services |
| Track as a customer | Enter only the order code from website checkout or WhatsApp at [Track order](https://ezzeldeenalkurd-001-site2.ftempurl.com/s/shadi-test/track) |

Please use fictional customer details. The demo owner account can change the demo business's data. Platform operator actions use a separate role.

## How it works

ASP.NET Core 8 MVC/Razor and Web API serve the portal, storefront, and dashboard. EF Core 9 stores business-scoped data in SQL Server. Checkout validates stock in a transaction; chat orders enter an owner-review state. The AI integration uses n8n, OpenAI, and SQL-backed tools to read current products and store facts. WhatsApp connects through Evolution API. See [`automation/`](automation/) for workflow exports, prompts, and their source generators; [`Database/005_bot_runtime.sql`](Database/005_bot_runtime.sql) and [`Database/006_bot_contracts.sql`](Database/006_bot_contracts.sql) define the bot's SQL boundary.

## Run the code locally

Install the .NET 8 SDK and SQL Server LocalDB, open [`CypherCommerce.sln`](CypherCommerce.sln) in Visual Studio, then select **LocalDB (Development)**. For an empty local database, run `dotnet run --project CypherCommerce.csproj --launch-profile "LocalDB (Development)" -- --initialize-database` once, followed by `dotnet run --project CypherCommerce.csproj --launch-profile "LocalDB (Development)"`. No business or customer data is seeded automatically. To run AI locally, apply the two bot SQL scripts, import the n8n exports, configure your own SQL/OpenAI/Evolution credentials, and set the app's `Automation` webhook URL and key settings. Secrets and deployment settings are intentionally absent from this repository.
