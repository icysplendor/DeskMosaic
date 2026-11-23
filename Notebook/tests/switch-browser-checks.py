"""Isolated real-editor checks for slow I/O, rapid selection and save safety.
Run after preview-server.mjs starts on 4173 and Chrome CDP starts on 9223.
Optional --baseline uses a separately bundled index-baseline.html for comparison.
"""
from pathlib import Path
import json, time, uuid, shutil, sys
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
root = Path('/tmp/notebook-ui-test-library')
c = CDP(); c.call('Page.enable'); c.call('Page.navigate', {'url': 'about:blank'})
if root.exists(): shutil.rmtree(root)
book = str(uuid.uuid4()); ids = [str(uuid.uuid4()) for _ in range(3)]
bookpath = root / 'books' / book
bookpath.mkdir(parents=True)
(bookpath/'book.json').write_text(json.dumps({'id':book,'title':'切换测试','created':0}))
(root/'.state.json').write_text(json.dumps({'grace':5000,'lastSync':0,'conflicts':0}))
for n, (title, marker) in enumerate([('Plain','PLAIN BODY'),('Images','IMAGE BODY'),('Last','LAST BODY')]):
 folder = bookpath / ids[n]; (folder/'reference').mkdir(parents=True); (folder/'.trash').mkdir()
 (folder/'note.json').write_text(json.dumps({'id':ids[n],'title':title,'created':0,'updated':n}))
 text = marker+'\n\n'
 if n == 0:
  text += ('```\n' + 'xray uuid\nroot@linux:/# xray x25519\n' * 20 + '```\n\n') * 8
 if n == 1:
  for j in range(5):
   (folder/'reference'/f'{j}.png').write_bytes(Path('MACAssistant/Assets.xcassets/AppIcon.appiconset/AppIcon-16.png').read_bytes())
   text += f'![image {j}](reference/{j}.png)\n\n'
 (folder/'main.md').write_text(text)
bridge = """
window.testSlow=false;window.imageReads=0;window.maxImageReads=0;
window.chrome=window.chrome||{};window.chrome.webview={postMessage:async message=>{
 const binary=message.op==='readBinary';
 if(binary) {window.imageReads++;window.maxImageReads=Math.max(window.maxImageReads,window.imageReads);}
 try {
  if(window.testSlow && (binary || message.op==='read'))
   await new Promise(r=>setTimeout(r,binary?500:100));
  const response=await fetch('/_bridge',{method:'POST',body:JSON.stringify(message)});
  const value=await response.json();window.nativeReply(message.id,value.result,value.error);
 }catch(error){window.nativeReply(message.id,null,error.message);}
 finally{if(binary)window.imageReads--;}
}};
"""
c.call('Page.addScriptToEvaluateOnNewDocument',{'source':bridge})
baseline = '--baseline' in sys.argv
c.call('Page.navigate',{'url':'http://127.0.0.1:4173/'+('index-baseline.html' if baseline else 'index.html')})
def wait(expression, timeout=12):
 end=time.monotonic()+timeout
 while time.monotonic()<end:
  if c.eval(expression): return
  time.sleep(.02)
 raise AssertionError('Timed out: '+expression)
def click(title):
 c.eval(f"[...document.querySelectorAll('.note-row')].find(x=>x.title==={json.dumps(title)}).click()")
def body(marker):
 return "document.querySelector('.vditor-ir')?.textContent.includes("+json.dumps(marker)+")"
wait("document.querySelectorAll('.note-row').length===3")
click('Last');wait(body('LAST BODY'));time.sleep(.3)
c.eval('window.testSlow=true;window.maxImageReads=0')
start=time.monotonic();click('Plain');wait(body('PLAIN BODY'))
plain_elapsed=(time.monotonic()-start)*1000
print(f'{"BASELINE" if baseline else "OPTIMIZED"} no images, eight code blocks, 100ms text reads: {plain_elapsed:.0f}ms until body')
time.sleep(.3)
start=time.monotonic();click('Images');wait(body('IMAGE BODY'))
elapsed=(time.monotonic()-start)*1000
wait("[...document.querySelectorAll('.vditor-ir img')].filter(x=>x.src.startsWith('data:image/png')).length===5")
wait('window.imageReads===0')
print(f'{"BASELINE" if baseline else "OPTIMIZED"} five-image note, 100ms text/500ms image reads: {elapsed:.0f}ms until body')
if not baseline:
 assert elapsed<900, 'Text must not wait for slow images'
 assert c.eval('window.maxImageReads')<=3, 'Image reads must be bounded and deduplicated'
 # Switch while image loads are still running. A stale request cannot repaint
 # another note, and the final click wins even when earlier reads complete late.
 click('Plain');wait(body('PLAIN BODY'))
 click('Images');wait(body('IMAGE BODY'))
 assert c.eval('window.imageReads')>0
 click('Last');wait(body('LAST BODY'));wait('window.imageReads===0')
 click('Plain');time.sleep(.02);click('Last');wait(body('LAST BODY'));time.sleep(.7)
 assert c.eval("document.getElementById('breadcrumb').textContent.endsWith('Last')")
 assert c.eval("document.querySelector('.vditor-ir img')===null")
 # Pending edits must reach the original file before selection changes.
 click('Plain');wait(body('PLAIN BODY'));wait("!document.getElementById('editor').inert")
 c.eval("document.getElementById('source-mode').click();var source=document.querySelector('.vditor-sv');source.focus();source.select()")
 c.call('Input.insertText',{'text':'SAVED BEFORE SWITCH\n'})
 click('Last');wait("document.querySelector('.vditor-sv').value.includes('LAST BODY')")
 wait("!document.getElementById('editor').inert")
 assert 'SAVED BEFORE SWITCH' in (bookpath/ids[0]/'main.md').read_text()
 # Catalog text is deliberately stale: disk must still be authoritative.
 (bookpath/ids[0]/'main.md').write_text('EXTERNAL UPDATE\n')
 click('Plain');wait("document.querySelector('.vditor-sv').value.includes('EXTERNAL UPDATE')")
 print('PASS bounded image I/O, final selection, pending save and fresh disk reads')
