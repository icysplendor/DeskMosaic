"""Isolated long-document checks. Preview 4173 and Chrome CDP 9223 required.
Optional --baseline uses index-baseline.html from the previous build.
"""
from pathlib import Path
import json, time, uuid, shutil, sys
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
c = CDP(); c.call('Page.enable'); c.call('Network.enable'); c.call('Network.setBypassServiceWorker',{'bypass':True}); c.call('Page.navigate', {'url':'about:blank'})
c.call('Emulation.setDeviceMetricsOverride', {'width':1440,'height':900,'deviceScaleFactor':1,'mobile':False})
root=Path('/tmp/notebook-ui-test-library'); shutil.rmtree(root,ignore_errors=True)
book=str(uuid.uuid4()); folder=root/'books'/book; folder.mkdir(parents=True)
(folder/'book.json').write_text(json.dumps({'id':book,'title':'长笔记验收','created':0}))
(root/'.state.json').write_text(json.dumps({'grace':5000,'lastSync':0,'conflicts':0}))
texts = {
 'Short':'SHORT BODY\n',
 'Prose':'\n\n'.join(f'Paragraph {i} '+ '这是一篇测试长笔记，用来验证阅读和编辑性能。'*8 for i in range(1000)),
 'Code':'\n\n'.join('```\n'+'\n'.join(f'unknown-config-{i} root@linux:/# xray x25519' for i in range(40))+'\n```' for n in range(80))+'\n\nLAST PARAGRAPH\n',
}
notes={}
for i,(title,text) in enumerate(texts.items()):
 note=folder/str(uuid.uuid4());note.mkdir();notes[title]=note
 (note/'note.json').write_text(json.dumps({'id':note.name,'title':title,'created':0,'updated':i}))
 (note/'main.md').write_text(text)
c.call('Page.addScriptToEvaluateOnNewDocument',{'source':"window.chrome={webview:{postMessage:async m=>{try{if(m.op==='ready')window.fixtureReady=true;const r=await fetch('/_bridge',{method:'POST',body:JSON.stringify(m)});const v=await r.json();window.nativeReply(m.id,v.result,v.error)}catch(e){window.nativeReply(m.id,null,e.message)}}}}"})
baseline='--baseline' in sys.argv
c.call('Page.navigate',{'url':'http://127.0.0.1:4173/'+('index-baseline.html' if baseline else 'index.html')})
def wait(expr):
 end=time.monotonic()+20
 while time.monotonic()<end:
  if c.eval(expr): return
  time.sleep(.03)
 raise AssertionError(expr)
def select(title):
 c.eval('[...document.querySelectorAll(".note-row")].find(x=>x.title==='+json.dumps(title)+').click()')
 wait('document.getElementById("breadcrumb").textContent.endsWith('+json.dumps(title)+') && !document.getElementById("editor").inert')
wait('window.fixtureReady && document.querySelectorAll(".note-row").length===3');select('Short');time.sleep(.4)
c.eval("window.highlightCalls=0;const h=hljs.highlight;hljs.highlight=function(...a){highlightCalls++;return h.apply(this,a)}") if not baseline else None
for title in ['Prose','Short','Code']:
 start=time.monotonic();select(title);elapsed=(time.monotonic()-start)*1000
 print(('BASELINE' if baseline else 'OPTIMIZED'), title, f'{elapsed:.0f}ms until editable',flush=True)
if baseline: sys.exit(0)
wait('document.querySelector(".vditor-ir__preview > code.hljs")!==null')
assert c.eval('highlightCalls')<15, 'offscreen blocks were all highlighted'
assert c.eval('document.querySelectorAll(".vditor-ir__preview > code").length')==80
assert c.eval('document.querySelector(".vditor-ir").textContent.includes("LAST PARAGRAPH")'), 'full document must exist before scrolling'
assert c.eval('!!document.querySelector("#vditorHljsStyle")'), 'initial code theme missing'
# Copy from a not-yet-highlighted block still uses its complete source.
c.eval('[...document.querySelectorAll(".vditor-copy button:not(.code-popout)")].at(-1).click()');time.sleep(.15)
assert Path('/tmp/notebook-ui-clipboard').read_text()=='\n'.join(f'unknown-config-{i} root@linux:/# xray x25519' for i in range(40))
c.eval('[...document.querySelectorAll(".vditor-ir__node[data-type=code-block]")].at(-1).scrollIntoView({block:"center"})')
wait('[...document.querySelectorAll(".vditor-ir__preview > code")].at(-1).classList.contains("hljs")')
# Lazy display work must not mutate saved Markdown or drop offscreen blocks.
c.eval('document.getElementById("source-mode").click()')
assert c.eval('document.querySelector(".vditor-sv").value').count('```')==160
c.eval('document.getElementById("preview-mode").click()')
wait('document.querySelector(".vditor-ir")?.textContent.includes("LAST PARAGRAPH")')
c.eval('let root=document.querySelector(".vditor-ir [contenteditable]");let p=[...root.querySelectorAll("p")].at(-1);root.focus();let r=document.createRange();r.selectNodeContents(p);r.collapse(false);getSelection().removeAllRanges();getSelection().addRange(r)')
c.call('Input.insertText',{'text':' SAVED AT END'});time.sleep(.3)
select('Short')
saved=(notes['Code']/'main.md').read_text()
assert saved.count('```')==160 and 'LAST PARAGRAPH SAVED AT END' in saved
select('Code');wait('document.querySelector(".vditor-ir").textContent.includes("SAVED AT END")')
# Switching quickly cancels work associated with the old editor DOM.
select('Short');time.sleep(.3)
assert c.eval('document.querySelector(".vditor-ir").textContent.includes("SHORT BODY") && !document.querySelector(".code-language")')
assert (notes['Prose']/'main.md').read_text()==texts['Prose'], 'viewing changed original prose'
print('PASS deferred highlighting; complete document/copy; bottom edit/save; mode switches; stale jobs; original prose unchanged')
