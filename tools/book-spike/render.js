const {chromium}=require('playwright');const fs=require('fs');const path=require('path');
const [,, htmlFile, pdfFile] = process.argv;
const fontRoot=path.join(__dirname,'..','..','src','Codex.Web','wwwroot');
(async()=>{
  const b=await chromium.launch(); const ctx=await b.newContext({javaScriptEnabled:false}); const p=await ctx.newPage();
  const blocked=[], served=[];
  await p.route('**',r=>{const u=new URL(r.request().url());
    if(u.host==='book.local'&&u.pathname.startsWith('/fonts/')){const f=path.join(fontRoot,u.pathname);if(fs.existsSync(f)){served.push(u.pathname);return r.fulfill({body:fs.readFileSync(f),contentType:'font/woff2'})}}
    if(r.request().url().startsWith('data:'))return r.continue();
    blocked.push(u.href); return r.abort();});
  const t0=Date.now();
  await p.setContent(fs.readFileSync(htmlFile,'utf8'),{waitUntil:'load'});
  await p.evaluateHandle('document.fonts.ready').catch(()=>{});
  const t1=Date.now();
  await p.pdf({path:pdfFile,preferCSSPageSize:true,printBackground:true,tagged:true,outline:true});
  console.log(JSON.stringify({chromium:b.version(),loadMs:t1-t0,pdfMs:Date.now()-t1,fontsServed:served.length,blocked,bytes:fs.statSync(pdfFile).size}));
  await b.close();})();
