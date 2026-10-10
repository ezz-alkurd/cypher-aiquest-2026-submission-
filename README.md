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

## Run the inspection demo from a fresh clone

On the team's Windows laptop, install the .NET 8 SDK and use its existing private .NET User Secrets for the hosted SQL password and automation webhooks. Then run:

```powershell
git clone https://github.com/ezz-alkurd/cypher-aiquest-2026-submission-.git
cd cypher-aiquest-2026-submission-
dotnet run --launch-profile "Hosted demo (this laptop)"
```

Open the local [platform](http://localhost:5280/), [demo storefront](http://localhost:5280/s/shadi-test), [owner sign-in](http://localhost:5280/admin/login), [owner dashboard](http://localhost:5280/admin), or [order tracking](http://localhost:5280/s/shadi-test/track). The WhatsApp number remains the [same live demo chat](https://wa.me/962797815644). The hosted demo needs internet access and the team's existing private secrets; they are not committed.

For a separate database, install SQL Server LocalDB and run `dotnet run --launch-profile "LocalDB (Development)" -- --initialize-database` once, then `dotnet run --launch-profile "LocalDB (Development)"`. This creates schema only. Local AI additionally needs the bot SQL scripts, n8n workflows, and private SQL/OpenAI/Evolution credentials. No business or customer data is seeded automatically.
