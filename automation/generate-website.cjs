'use strict';
const {sqlNode}=require('./sql-node-config.cjs');
const fs=require('node:fs');
const path=require('node:path');
const {randomUUID}=require('node:crypto');
const common=require('./runtime.cjs');
const website=require('./website-runtime.cjs');
const dir=__dirname;
const ids=JSON.parse(fs.readFileSync(path.join(dir,'workflow-ids.json'),'utf8'));
const source=fs.readFileSync(path.join(dir,'runtime.cjs'),'utf8');
const prefix=source.slice(0,source.indexOf('function normalizeWebhook'));
const definitions=Object.fromEntries(Object.entries({...common,...website}).filter(([key,value])=>typeof value==='function'&&!['arabic','confirmation'].includes(key)).map(([key,value])=>[key,value.toString()]));
function embed(body) {
  const selected=new Set();
  function collect(code){for(const [name,source] of Object.entries(definitions))if(new RegExp('\\b'+name+'\\s*\\(').test(code)&&!selected.has(name)){selected.add(name);collect(source);}}
  collect(body);
  return prefix+'\n'+[...selected].map(key=>definitions[key]).join('\n\n')+'\n'+body;
}
const expr=value=>'={{ '+value+' }}';
const ref=name=>'$('+JSON.stringify(name)+').first().json';
const scope={storeId:'number',sessionId:'string',conversationId:'string',leaseToken:'string',messageId:'string'};
const request={storefrontSlug:'string',visitorSessionId:'string',requestId:'string',text:'string'};
const catalog={...scope,mode:'string',query:'string',page:'number',pageSize:'number',availableOnly:'boolean'};
const names={websiteRoute:'Cypher - Route Website Session',websiteCatalog:'Cypher TOOL - Website Catalog',website:'website assistant'};
function mapping(schema,values){return {mappingMode:'defineBelow',value:values,matchingColumns:[],schema:Object.entries(schema).map(([id,type])=>({id,displayName:id,type,required:false,defaultMatch:false,display:true,canBeUsedToMatch:true,removed:false})),attemptToConvertTypes:false,convertFieldsToString:false};}
class Flow {
  constructor(key,description){this.key=key;this.json={name:names[key],active:false,nodes:[],connections:{},settings:{executionOrder:'v1',executionTimeout:90,saveExecutionProgress:false,saveManualExecutions:false,saveDataSuccessExecution:'none',saveDataErrorExecution:'none'},pinData:{},description};
    this.node('Integration notes','n8n-nodes-base.stickyNote',1,{content:'## '+names[key]+'\n'+description+'\n\n**SQL configured; complete end-to-end testing before activation.**\nWebsite backend integration: WEBSITE-ASSISTANT.md and SQL-INTEGRATION.md. No Firestore.\nCredential references contain no keys.',width:900,height:260},[-80,-330]);}
  node(name,type,typeVersion,parameters,position,extra={}){this.json.nodes.push({id:randomUUID(),name,type,typeVersion,parameters,position,...extra});return name;}
  code(name,body,position){return this.node(name,'n8n-nodes-base.code',2,{jsCode:embed(body)},position,{onError:'continueErrorOutput'});}
  safe(name,body,position){return this.node(name,'n8n-nodes-base.code',2,{jsCode:body},position);}
  input(schema){this.node('Input','n8n-nodes-base.executeWorkflowTrigger',1.1,{inputSource:'workflowInputs',workflowInputs:{values:Object.entries(schema).map(([name,type])=>({name,type}))}},[0,0]);}
  sql(name,contract,position){this.node(name,'n8n-nodes-base.microsoftSql',1.2,sqlNode(name).parameters,position,{alwaysOutputData:true,onError:'continueErrorOutput',credentials:sqlNode(name).credentials,notes:sqlNode(name).notes,notesInFlow:true});}
  edge(from,to,output=0,type='main'){const c=this.json.connections[from]||={};const ports=c[type]||=[];while(ports.length<=output)ports.push([]);ports[output].push({node:to,type,index:0});}
  chain(...nodes){for(let i=1;i<nodes.length;i++)this.edge(nodes[i-1],nodes[i]);}
  branch(name,condition,position){this.node(name,'n8n-nodes-base.if',2.3,{conditions:{options:{caseSensitive:true,leftValue:'',typeValidation:'strict',version:1},conditions:[{leftValue:expr(condition),rightValue:true,operator:{type:'boolean',operation:'equals'}}],combinator:'and'},options:{}},position);}
  call(name,key,schema,values,position,tool=false,description=''){this.node(name,tool?'@n8n/n8n-nodes-langchain.toolWorkflow':'n8n-nodes-base.executeWorkflow',tool?2.2:1.3,{...(tool?{description}:{}),workflowId:{__rl:true,mode:'id',value:ids[key]||'BIND_'+key.toUpperCase()},workflowInputs:mapping(schema,values),...(!tool?{mode:'each',options:{waitForSubWorkflow:true}}:{})},position,tool?{}:{onError:'continueErrorOutput'});}
  save(){if(ids[this.key])this.json.id=ids[this.key];fs.writeFileSync(path.join(dir,this.key+'.json'),JSON.stringify(this.json,null,2)+'\n');}
}
const flows=[];
{
  const f=new Flow('websiteRoute','Resolves a public storefront, validates an anonymous backend-owned visitor session, rate-limits, replays saved replies, and leases one website turn.');flows.push(f);
  f.input(request);f.sql('SQL Resolve website session','website.route: verified storefront slug, backend visitor session, request idempotency, exclusive lease, last 12 public history turns',[300,0]);
  f.code('Verify website route',`return [{json:websiteRoute(payload($input.all().map(i=>i.json)),${ref('Input')})}];`,[620,0]);
  f.safe('Route unavailable',"return [{json:{ok:false,httpStatus:503,body:{success:false,error:'integration_unavailable',message:'المساعد غير متاح حالياً. جرّب بعد شوي.'}}}];",[620,220]);
  f.chain('Input','SQL Resolve website session','Verify website route');f.edge('SQL Resolve website session','Route unavailable',1);f.edge('Verify website route','Route unavailable',1);
}
{
  const f=new Flow('websiteCatalog','Read-only public browsing lists every matching variant through bounded pages, with authoritative prices, availability, images and product links.');flows.push(f);
  f.input(catalog);f.sql('SQL Read public website catalog','website.catalog: active website lease and conversation verified, complete active scoped catalog (max 5000), public URLs; channel website',[300,0]);
  f.code('Return catalog page',`return [{json:websiteCatalog(${ref('Input')},payload($input.all().map(i=>i.json)))}];`,[620,0]);
  f.safe('Catalog unavailable',"return [{json:{success:false,code:'integration_unavailable',message:'تعذر التحقق من الأصناف حالياً.'}}];",[620,220]);
  f.chain('Input','SQL Read public website catalog','Return catalog page');f.edge('SQL Read public website catalog','Catalog unavailable',1);f.edge('Return catalog page','Catalog unavailable',1);
}
{
  const f=new Flow('website','One shared customer-facing website assistant. Uses existing Store Info and a paginated public catalog; returns text plus grounded product cards. Read-only: no carts, orders or WhatsApp sends.');flows.push(f);
  f.node('Website message','n8n-nodes-base.webhook',2.1,{httpMethod:'POST',path:'cypher-website-assistant-v1',authentication:'headerAuth',responseMode:'responseNode',options:{}},[0,0],{credentials:{httpHeaderAuth:{id:'BgKjkAjOEtkd439C',name:'Cypher Website Backend'}},webhookId:randomUUID(),notes:'Backend-to-n8n only. Dedicated Cypher backend Header Auth. Never give the browser this credential.'});
  f.code('Validate website message',`return [{json:websiteInput($input.first().json.body)}];`,[270,0]);
  f.branch('Valid message',ref('Validate website message')+'.valid === true',[540,0]);
  f.call('Resolve website conversation','websiteRoute',request,Object.fromEntries(Object.keys(request).map(key=>[key,expr(ref('Validate website message')+'.'+key)])),[810,-80]);
  f.branch('Website route ready',ref('Resolve website conversation')+'.ok === true',[1080,-80]);
  f.safe('Trusted website turn','return [{json:'+ref('Resolve website conversation')+'}];',[1350,-160]);
  f.branch('Replay saved response',ref('Trusted website turn')+'.replay === true',[1620,-160]);
  f.node('Website AI Agent','@n8n/n8n-nodes-langchain.agent',3,{promptType:'define',text:expr("JSON.stringify({previousTurns:"+ref('Trusted website turn')+".history,currentMessage:"+ref('Trusted website turn')+".text})"),options:{systemMessage:fs.readFileSync(path.join(dir,'website-system-message.md'),'utf8'),maxIterations:6,returnIntermediateSteps:true,enableStreaming:false}},[1890,-80],{onError:'continueErrorOutput'});
  f.node('OpenAI Website Model','@n8n/n8n-nodes-langchain.lmChatOpenAi',1.3,{model:{__rl:true,mode:'list',value:'gpt-5-mini'},builtInTools:{},options:{}},[1670,200],{credentials:{openAiApi:{id:'i2XTDMGs8ysVzcXQ',name:'OpenAI account'}},retryOnFail:true,maxTries:2,waitBetweenTries:1000});
  const fixed=Object.fromEntries(Object.keys(scope).map(key=>[key,expr(ref('Trusted website turn')+'.'+key)]));
  f.call('Store_Info','store',scope,fixed,[1930,200],true,'Read public facts of the current store: location, hours, contact, delivery areas/policy, returns and ordering policy. Use for general store questions and greeting context. Missing facts are unknown; never infer no delivery from null. Scope comes from the backend and cannot be chosen by the AI.');
  const ai=(key,description,type='string',value='')=>expr('$fromAI('+JSON.stringify(key)+', '+JSON.stringify(description)+', '+JSON.stringify(type)+', '+JSON.stringify(value)+')');
  f.call('Search_Catalog','websiteCatalog',catalog,{...fixed,mode:ai('mode','search for products or categories for a list of departments','string','search'),query:ai('query','Short product/category/brand term, e.g. صوص. Do not pass a whole question. Keep the previous term for follow-ups.'),page:ai('page','Page starting at 1; use the next page only when the customer asks for more','number',1),pageSize:12,availableOnly:ai('availableOnly','true for currently available variants; false when asked about sold-out or all matching products','boolean',true)},[2190,200],true,'Browse matching product variants, not a single order selection. search checks name, category, aliases and description, returns up to 12 product cards, totalCount, page and hasMore. No hidden truncation. categories returns departments. not_found means no matching product, out_of_stock means matches exist but none are available. Results contain current prices and safe public URLs; do not expose stock quantities.');
  for(const [name,type] of [['OpenAI Website Model','ai_languageModel'],['Store_Info','ai_tool'],['Search_Catalog','ai_tool']])f.edge(name,'Website AI Agent',0,type);
  f.code('Build grounded website response',`return [{json:websiteReply($input.first().json,${ref('Trusted website turn')})}];`,[2190,-80]);
  f.safe('Prepare leased failure','const c='+ref('Trusted website turn')+";return [{json:{...c,httpStatus:503,body:{success:false,requestId:c.messageId,conversationId:c.conversationId,error:'verification_unavailable',message:'ما قدرت أتحقق من معلومات المحل حالياً. جرّب بعد شوي.'}}}];",[2190,420]);
  f.node('Website reply candidate','n8n-nodes-base.noOp',1,{},[2480,-80]);
  f.sql('SQL Save website turn','website.finish: save exact grounded response and role-tagged history, request ledger, release only matching active lease; failed responses are not cached; echo full scope, finished, responseSaved',[2770,-80]);
  f.code('Verify saved website turn',`return [{json:websiteCommitted(payload($input.all().map(i=>i.json)),${ref('Website reply candidate')})}];`,[3060,-80]);
  f.safe('Website service unavailable',"return [{json:{httpStatus:503,body:{success:false,error:'integration_unavailable',message:'المساعد غير متاح حالياً. جرّب بعد شوي.'}}}];",[2770,500]);
  f.node('Respond to website','n8n-nodes-base.respondToWebhook',1.5,{respondWith:'json',responseBody:expr('$json.body'),options:{responseCode:expr('$json.httpStatus'),responseHeaders:{entries:[{name:'Cache-Control',value:'no-store'}]}}},[3390,80]);
  f.node('Session and cards','n8n-nodes-base.stickyNote',1,{content:'## Public website chat, separate from WhatsApp\nBackend owns anonymous visitor identity and storefront mapping. AI cannot select StoreId/session/lease.\nSQL route loads 12 public history turns; no shared in-process memory. Prices and stock must be reread by tools every turn.\nCards are extracted from actual Search_Catalog observations, never AI-invented JSON.\nSave the exact response before returning 200, replay by the same requestId, TTL-recover abandoned leases.\nSQL Store Info must accept website leases as well as WhatsApp leases. See WEBSITE-ASSISTANT.md.',width:1120,height:270},[1620,680]);
  f.chain('Website message','Validate website message','Valid message','Resolve website conversation','Website route ready','Trusted website turn','Replay saved response');
  f.edge('Valid message','Respond to website',1);f.edge('Website route ready','Respond to website',1);
  f.edge('Replay saved response','Respond to website');f.edge('Replay saved response','Website AI Agent',1);
  f.chain('Website AI Agent','Build grounded website response','Website reply candidate','SQL Save website turn','Verify saved website turn','Respond to website');
  f.edge('Website AI Agent','Prepare leased failure',1);f.edge('Build grounded website response','Prepare leased failure',1);f.edge('Prepare leased failure','Website reply candidate');
  for(const name of ['Validate website message','Resolve website conversation','SQL Save website turn','Verify saved website turn'])f.edge(name,'Website service unavailable',1);
  f.edge('Website service unavailable','Respond to website');
}
for(const flow of flows)flow.save();
console.log('Generated 3 inactive website workflows.');
