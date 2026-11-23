"""DAV XML parsing and bounded directory scans in a real DOMParser.
Build: esbuild src/webdav.mjs --bundle --format=iife --global-name=WebDAVList
       --outfile=/tmp/notebook-webdav-test.js
"""
from pathlib import Path
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
c=CDP();c.call('Page.enable');c.call('Page.navigate',{'url':'about:blank'})
c.eval(Path('/tmp/notebook-webdav-test.js').read_text())
result=c.eval('''(async()=>{
 const base='https://cloud.example/remote.php/dav/files/user/Notes/';
 const encode=text=>btoa(unescape(encodeURIComponent(text)));
 const response=(path,folder,etag='')=>`<d:response><d:href>${base}${path}</d:href><d:propstat><d:prop><d:resourcetype>${folder?'<d:collection/>':''}</d:resourcetype><d:getetag>${etag}</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat><d:propstat><d:prop><d:unsupported/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>`;
 const files={ '':['books/'], 'books/':['books/a/','books/b/','books/c/','books/d/'],
 'books/a/':['books/a/hello%20world.md'], 'books/b/':['books/b/main.md'],
 'books/c/':['books/c/main.md'], 'books/d/':['books/d/main.md'] };
 let active=0,max=0,calls=0;
 const request=async(method,path,data,headers)=>{
  if(method!=='PROPFIND'||headers.Depth!=='1')throw Error('wrong DAV method');
  active++;max=Math.max(max,active);calls++;
  await new Promise(r=>setTimeout(r,15));active--;
  return {status:207,data:encode('<d:multistatus xmlns:d="DAV:">'+response(path,true)+
    files[path].map(p=>response(p,p.endsWith('/'),'&quot;version1&quot;')).join('')+
    response('books/evil.md',false,'&quot;v&quot;').replace('cloud.example','other.example')+ '</d:multistatus>')};
 };
 const list=await WebDAVList.listRemoteFiles(request,base);
 if(list.length!==4||list[0].etag!=='"version1"'||!list.some(x=>x.path==='books/a/hello world.md')||max!==3)throw Error('bad metadata or concurrency');
 for(const bad of ['<broken','<root/>','<d:multistatus xmlns:d="DAV:"><d:response><d:href>'+base+'books/unknown</d:href><d:propstat><d:prop><d:getetag>v</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>','<d:multistatus xmlns:d="DAV:"><d:response><d:href>'+base+'books/main.md</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 403 Forbidden</d:status></d:propstat></d:response></d:multistatus>']){
  let rejected=false;try{await WebDAVList.listRemoteFiles(async()=>({status:207,data:encode(bad)}),base)}catch(e){rejected=true}
  if(!rejected)throw Error('invalid listing treated as deleted files');
 }
 for(const body of ['',response('books/main.md',false),response('',true).replace(/<d:href>.*?<\/d:href>/,'')]){
  let rejected=false;
  try{await WebDAVList.listRemoteFiles(async()=>({status:207,data:encode('<d:multistatus xmlns:d="DAV:">'+body+'</d:multistatus>')}),base)}catch{rejected=true}
  if(!rejected)throw Error('incomplete inventory accepted');
 }
 const empty=await WebDAVList.listRemoteFiles(async()=>({status:207,data:encode('<d:multistatus xmlns:d="DAV:">'+response('',true)+'</d:multistatus>')}),base);
 if(empty.length)throw Error('valid empty collection must parse; engine applies deletion guard');
 const partial=await WebDAVList.listRemoteFiles(async(...args)=>{
  if(args[1]==='books/a/')throw Object.assign(Error('folder denied'),{code:'DAV_ERROR',status:403});
  return request(...args);
 },base);
 if(partial.length!==3||partial.errors.length!==1||partial.errors[0].path!=='books/a/'||partial.errors[0].status!==403)throw Error('independent subtree scan interrupted');
 const malformed=await WebDAVList.listRemoteFiles(async(...args)=>{
  if(args[1]==='books/a/')return {status:207,data:encode('<d:multistatus xmlns:d="DAV:">'+response('books/a/main.md',false,'&quot;v&quot;')+'</d:multistatus>')};
  return request(...args);
 },base);
 if(malformed.some(x=>x.path.startsWith('books/a/'))||malformed.errors.length!==1)throw Error('partial malformed directory was published');
 let unauthorized=false;try{await WebDAVList.listRemoteFiles(async(...args)=>{if(args[1]==='books/a/')throw Object.assign(Error('expired identity'),{status:401});return request(...args)},base)}catch(e){unauthorized=e.status===401}
 if(!unauthorized)throw Error('expired identity must stop the whole scan');
 return {files:list.length,calls,max,independent:partial.length};
})()''')
print('PASS DAV namespaces, ETags, escaped paths, failed properties and malformed XML; scan:',result)
