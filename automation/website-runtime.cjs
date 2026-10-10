'use strict';
const {arabic, verifyScope, payload} = require('./runtime.cjs');
const insist = (condition, code) => { if (!condition) throw new Error(code); };
const text = (value, max = 1000) => typeof value === 'string' ? value.trim().slice(0, max) : '';
const integer = (value, min = 0, max = 1000000000) => {
  insist(Number.isSafeInteger(Number(value)) && Number(value) >= min && Number(value) <= max, 'invalid_integer');
  return Number(value);
};
const money = cents => (integer(cents) / 100).toFixed(2);

function websiteInput(body) {
  const validId = v => typeof v === 'string' && /^[A-Za-z0-9_-]{16,128}$/.test(v);
  const valid = body && !Array.isArray(body) &&
    typeof body.storefrontSlug === 'string' && /^[A-Za-z0-9_-]{1,100}$/.test(body.storefrontSlug) &&
    validId(body.visitorSessionId) && validId(body.requestId) &&
    typeof body.text === 'string' && body.text.trim().length > 0 && body.text.length <= 2000;
  if (!valid) return {valid:false,httpStatus:400,body:{success:false,error:'validation_error',message:'ابعث رسالة نصية قصيرة مع جلسة صالحة.'}};
  // Identity and history from the browser are discarded. Only the backend may call the webhook.
  return {valid:true,storefrontSlug:body.storefrontSlug,visitorSessionId:body.visitorSessionId,
    requestId:body.requestId,text:body.text.trim(),channel:'website'};
}

function websiteScope(result, input) {
  verifyScope(result, input);
  for (const key of ['conversationId','messageId','leaseToken']) insist(result[key] === input[key], 'website_scope_mismatch');
  insist(result.channel === 'website', 'wrong_channel');
  return result;
}

function websiteRoute(result, input) {
  if (result.ok !== true) {
    const errors = {store_not_found:404,assistant_disabled:403,session_conflict:403,request_conflict:409,
      conversation_busy:409,rate_limit_exceeded:429};
    const code = Object.hasOwn(errors,result.code) ? result.code : 'integration_unavailable';
    return {ok:false,httpStatus:errors[code]||503,body:{success:false,error:code,message:code==='rate_limit_exceeded'?'خفف الرسائل شوي وجرّب كمان مرة.':'المساعد غير متاح حالياً، جرّب بعد شوي.'}};
  }
  insist(Number.isSafeInteger(result.storeId) && result.storeId>0,'store_scope_missing');
  insist(result.storefrontSlug===input.storefrontSlug && result.visitorSessionId===input.visitorSessionId,'website_route_mismatch');
  insist(result.messageId===input.requestId && result.channel==='website','website_request_mismatch');
  insist(result.storeActive===true && result.assistantEnabled===true,'website_store_unavailable');
  insist(text(result.sessionId,200) && text(result.conversationId,200),'website_session_missing');
  const context={ok:true,storeId:result.storeId,sessionId:result.sessionId,conversationId:result.conversationId,
    messageId:input.requestId,leaseToken:result.leaseToken,storefrontSlug:input.storefrontSlug,
    visitorSessionId:input.visitorSessionId,channel:'website',text:input.text};
  if(result.replay===true) {
    insist(result.acquired===false && result.savedResponse?.success===true,'invalid_replay');
    insist(result.savedResponse.requestId===input.requestId && result.savedResponse.conversationId===result.conversationId,'replay_scope_mismatch');
    return {...context,replay:true,httpStatus:200,body:result.savedResponse};
  }
  insist(result.acquired===true && text(result.leaseToken,200),'website_lease_missing');
  insist(Array.isArray(result.history),'website_history_missing');
  const history=result.history.slice(-12).map(turn=>{
    insist(['user','assistant'].includes(turn.role) && typeof turn.content==='string','invalid_history');
    return {role:turn.role,content:text(turn.content,1500)};
  });
  return {...context,replay:false,acquired:true,history};
}

function publicUrl(value, image=false) {
  if(typeof value!=='string' || value.length>2000 || /[\s\\<>\u0000-\u001f]/.test(value)) return null;
  if(value.startsWith('/') && !value.startsWith('//') && !/%(?:2f|5c|00|0a|0d)/i.test(value)) return value;
  if(image && /^https:\/\/[A-Za-z0-9.-]+(?::\d+)?(?:\/[^\s]*)?$/.test(value)) return value;
  return null;
}

function websiteCatalog(input, result) {
  websiteScope(result,input);
  insist(result.success===true && Array.isArray(result.products),'catalog_unavailable');
  insist(['search','categories'].includes(input.mode),'invalid_catalog_mode');
  // The SQL contract returns the current store's complete public catalog, not a pre-truncated search.
  insist(result.catalogComplete===true && result.products.length<=5000,'incomplete_catalog');
  const products=result.products.filter(p=>p.isActive===true).map(p=>{
    insist(p.storeId===input.storeId,'cross_store_product');
    insist(typeof p.name==='string' && p.name.trim().length>0,'product_name_missing');
    return {...p,productId:integer(p.productId,1),priceCents:integer(p.priceCents),stockQuantity:integer(p.stockQuantity)};
  });
  const scope={storeId:input.storeId,sessionId:input.sessionId,conversationId:input.conversationId,messageId:input.messageId};
  if(input.mode==='categories') return {success:true,kind:'website_catalog',scope,mode:'categories',categories:[...new Set(products.map(p=>text(p.category,100)).filter(Boolean))].sort()};
  const query=arabic(input.query);
  insist(query && query.length<=200,'empty_catalog_query');
  const tokens=query.split(' ').filter(Boolean);
  const aliases=p=>Array.isArray(p.aliases)?p.aliases:String(p.aliases||'').split(',');
  const searchable=p=>arabic([p.name,p.sku,p.category,p.description,...aliases(p)].filter(Boolean).join(' '));
  const matched=products.filter(p=>tokens.every(token=>searchable(p).includes(token)));
  const available=matched.filter(p=>p.stockQuantity>0);
  const visible=input.availableOnly!==false?available:matched;
  const page=integer(input.page||1,1,5000),pageSize=integer(input.pageSize||12,1,20);
  const ordered=visible.sort((a,b)=>arabic(a.name).localeCompare(arabic(b.name))||a.productId-b.productId);
  const cards=ordered.slice((page-1)*pageSize,page*pageSize).map(p=>({productId:p.productId,name:text(p.name,200),
    category:text(p.category,100)||null,description:text(p.description,300)||null,unit:text(p.unit,50)||null,
    priceCents:p.priceCents,unitPrice:money(p.priceCents),currency:'JOD',available:p.stockQuantity>0,
    imageUrl:publicUrl(p.imageUrl,true),productUrl:publicUrl(p.productUrl)}));
  return {success:true,kind:'website_catalog',scope,mode:'search',query:text(input.query,200),availableOnly:input.availableOnly!==false,
    status:matched.length===0?'not_found':available.length===0?'out_of_stock':'found',
    totalCount:visible.length,matchedCount:matched.length,availableCount:available.length,page,pageSize,
    hasMore:page*pageSize<visible.length,products:cards};
}

function websiteObservation(value) {
  if(typeof value==='string') {insist(value.length<=200000,'tool_output_too_large');value=JSON.parse(value);}
  if(Array.isArray(value)) {insist(value.length===1,'unexpected_tool_rows');value=value[0];}
  insist(value && typeof value==='object' && !Array.isArray(value),'tool_output_missing');
  return value;
}

function websiteReply(agent, context) {
  insist(typeof agent.output==='string' && agent.output.trim().length>0 && agent.output.length<=6000,'invalid_agent_reply');
  insist(Array.isArray(agent.intermediateSteps) && agent.intermediateSteps.length>0 && agent.intermediateSteps.length<=12,'ungrounded_reply');
  let catalog=null;
  for(const step of agent.intermediateSteps) {
    insist(['Search_Catalog','Store_Info'].includes(step.action?.tool),'unknown_tool');
    const observation=websiteObservation(step.observation);
    insist(observation.success===true,'tool_unavailable');
    if(step.action.tool==='Search_Catalog') {
      insist(observation.kind==='website_catalog' && observation.scope,'catalog_proof_missing');
      for(const key of ['storeId','sessionId','conversationId','messageId']) insist(observation.scope[key]===context[key],'catalog_proof_scope_mismatch');
      catalog=observation;
    } else insist(observation.store && typeof observation.store==='object','store_facts_missing');
  }
  const cards=catalog?.mode==='search'?catalog.products:[];
  insist(Array.isArray(cards) && cards.length<=20,'invalid_cards');
  // Product cards come only from actual tool observations, never from the model's text or invented JSON.
  const body={success:true,requestId:context.messageId,conversationId:context.conversationId,
    reply:agent.output.trim(),products:cards,
    catalog:catalog?{mode:catalog.mode,query:catalog.query||null,availableOnly:catalog.availableOnly??null,status:catalog.status||null,
      totalCount:catalog.totalCount??null,page:catalog.page??null,pageSize:catalog.pageSize??null,
      hasMore:catalog.hasMore===true,categories:catalog.categories||[]}:null};
  return {...context,httpStatus:200,body};
}

function websiteCommitted(result, candidate) {
  websiteScope(result,candidate);
  insist(result.success===true && result.finished===true && result.messageId===candidate.messageId,'website_turn_not_saved');
  if(candidate.body.success) insist(result.responseSaved===true,'website_reply_not_saved');
  return {httpStatus:candidate.httpStatus,body:candidate.body};
}

module.exports={websiteInput,websiteScope,websiteRoute,publicUrl,websiteCatalog,websiteObservation,websiteReply,websiteCommitted};
