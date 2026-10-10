'use strict';
// Dedicated credential reference only. No secrets are stored in this module or exports.
const credential={id:'wYmJA7GzXMqRNPUE',name:'Cypher SQL Server'};
const contracts={
 'SQL Resolve store and lease':['route','Input'],
 'SQL Read store facts':['store','Input'],
 'SQL Read current inventory':['inventory','Input'],
 'SQL Read cart and live products':['cart.read','Input'],
 'SQL Commit cart version':['cart.commit','Validate cart action'],
 'SQL Read confirmation snapshot':['confirm.read','Input'],
 'SQL Commit review order':['confirm.commit','Validate explicit checkout'],
 'SQL Claim reply delivery':['send.claim','Input'],
 'SQL Finalize delivery':['send.finalize','Classify delivery'],
 'SQL Finish turn and release lease':['finish','Input'],
 'SQL Authorize store owner':['onboarding.authorize','Read website request'],
 'SQL Save pending pairing':['onboarding.pending','Sanitize QR response'],
 'SQL Mark WhatsApp ready':['onboarding.ready','Check persisted webhook'],
 'SQL Resolve website session':['website.route','Input'],
 'SQL Read public website catalog':['website.catalog','Input'],
 'SQL Save website turn':['website.finish','Website reply candidate'],
};
function sqlNode(name){
 if(!Object.hasOwn(contracts,name))throw new Error('Unknown SQL contract: '+name);
 const [operation,source]=contracts[name];
 return {parameters:{operation:'executeQuery',query:`EXEC dbo.CypherBot @operation=N'${operation}', @input=$1;`,
   options:{queryReplacement:`={{ [JSON.stringify($(${JSON.stringify(source)}).first().json)] }}`}},
   credentials:{microsoftSql:credential},
   notes:`${operation}: parameterized JSON boundary dbo.CypherBot. Install Database/005_bot_runtime.sql and 006_bot_contracts.sql first; SQL-INTEGRATION.md. Dedicated Cypher SQL credential; keep inactive until integration tests pass.`};
}
if(require.main===module){
 const fs=require('node:fs'),path=require('node:path'); let count=0;
 for(const file of fs.readdirSync(__dirname).filter(f=>f.endsWith('.json')&&f!=='workflow-ids.json')){
  const filename=path.join(__dirname,file),flow=JSON.parse(fs.readFileSync(filename,'utf8'));
  if(!Array.isArray(flow.nodes))continue;
  for(const node of flow.nodes){if(node.type!=='n8n-nodes-base.microsoftSql')continue; Object.assign(node,sqlNode(node.name));count++;}
  flow.active=false;
  for(const node of flow.nodes.filter(n=>n.type==='n8n-nodes-base.stickyNote')){
   node.parameters.content=node.parameters.content.replace(/\*\*Draft: SQL queries and credentials intentionally empty\.\*\*/g,'**SQL configured; runtime migration and integration testing required before activation.**')
    .replace(/\*\*Draft — SQL Server not connected by request\.\*\*/g,'**SQL configured; integration testing required before activation.**')
    .replace(/Do not publish before Ezz implements the SQL contracts\./g,'Do not publish before the SQL integration and two-store tests pass.');
  }
  fs.writeFileSync(filename,JSON.stringify(flow,null,2)+'\n');
 }
 if(count!==16)throw new Error(`Expected 16 SQL nodes, found ${count}`);
 console.log('Configured 16 SQL nodes; workflows remain inactive.');
}
module.exports={sqlNode,contracts,credential};
