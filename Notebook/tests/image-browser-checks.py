"""Real image selection/deletion, cross-mode undo and attachment recovery."""
from pathlib import Path
import json,time,uuid,shutil
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
c=CDP();c.call('Page.enable');c.call('Network.enable');c.call('Network.setBypassServiceWorker',{'bypass':True});c.call('Page.navigate',{'url':'about:blank'})
root=Path('/tmp/notebook-ui-test-library')
if root.exists():shutil.rmtree(root)
book,note=str(uuid.uuid4()),str(uuid.uuid4());folder=root/'books'/book/note
(folder/'reference').mkdir(parents=True);(folder/'.trash').mkdir()
(folder.parent/'book.json').write_text(json.dumps({'id':book,'title':'图片测试','created':0}))
(folder/'note.json').write_text(json.dumps({'id':note,'title':'图片笔记','created':0,'updated':0}))
image=folder/'reference'/'test.png';image.write_bytes(Path('MACAssistant/Assets.xcassets/AppIcon.appiconset/AppIcon-256.png').read_bytes())
md='before\n\n![photo](reference/test.png)\n\nafter\n';(folder/'main.md').write_text(md)
(root/'.state.json').write_text(json.dumps({'grace':5000,'lastPage':{'noteId':note,'bookId':book,'filter':'all'}}))
c.call('Page.addScriptToEvaluateOnNewDocument',{'source':"window.openedImagePaths=[];window.chrome=window.chrome||{};window.chrome.webview={postMessage:async message=>{try{if(message.op==='viewImage'){window.openedImagePaths.push(message.args.path);window.nativeReply(message.id,null,window.failImageOpen?'测试：图片查看器不可用':null);return;}let r=await fetch('/_bridge',{method:'POST',body:JSON.stringify(message)}),v=await r.json();window.nativeReply(message.id,v.result,v.error);}catch(e){window.nativeReply(message.id,null,e.message);}}};"})
c.call('Emulation.setDeviceMetricsOverride',{'width':1440,'height':900,'deviceScaleFactor':1,'mobile':False})
c.call('Page.navigate',{'url':'http://127.0.0.1:4173/index.html'})
def wait(expr,timeout=8):
 end=time.monotonic()+timeout
 while time.monotonic()<end:
  if c.eval(expr):return
  time.sleep(.05)
 raise AssertionError(expr)
def click_image():
 wait("document.querySelector('.vditor-ir img')?.naturalWidth===256")
 r=c.eval("(()=>{let r=document.querySelector('.vditor-ir img').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()")
 for typ in ['mousePressed','mouseReleased']:c.call('Input.dispatchMouseEvent',{'type':typ,'x':r['x'],'y':r['y'],'button':'left','clickCount':1})
 wait("!!document.querySelector('.notebook-image-selected')")
 assert c.eval("[...document.querySelectorAll('[data-type=img] .vditor-ir__marker')].every(x=>x.getBoundingClientRect().width===0)")
def key(value,number,modifiers=0):
 for typ in ('keyDown','keyUp'):c.call('Input.dispatchKeyEvent',{'type':typ,'key':value,'code':value,'windowsVirtualKeyCode':number,'modifiers':modifiers})
wait("document.querySelector('.vditor-ir img')?.naturalWidth===256")
for theme in ('light','dark'):
 if c.eval('document.documentElement.dataset.theme')!=theme:c.eval("document.getElementById('theme').click()")
 click_image()
 r=c.eval("(()=>{let r=document.querySelector('.vditor-ir img').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()")
 count=c.eval('window.openedImagePaths.length')
 for typ in ['mousePressed','mouseReleased']:c.call('Input.dispatchMouseEvent',{'type':typ,'x':r['x'],'y':r['y'],'button':'left','clickCount':2})
 wait(f'window.openedImagePaths.length==={count+1}')
 assert c.eval('window.openedImagePaths.at(-1)')==f'books/{book}/{note}/reference/test.png'
 assert not c.eval("!!document.querySelector('.vditor-img')")
 c.eval("document.getElementById('source-mode').click()")
 wait("!!document.querySelector('.vditor-sv')?.value.includes('![photo](reference/test.png)')")
 c.eval("document.getElementById('preview-mode').click()")
 print(f'PASS {theme} real double-click requests original image in external viewer; editor stays usable')
 for delete,number in [('Backspace',8),('Delete',46)]:
  click_image();key(delete,number);wait("!document.querySelector('.vditor-ir [data-type=img]')")
  c.eval('window.notebookFlush()')
  saved=(folder/'main.md').read_text()
  assert 'before' in saved and 'after' in saved and 'photo' not in saved and 'reference/' not in saved and '![' not in saved
  c.eval("document.getElementById('source-mode').click()")
  key('z',90,2);wait("document.querySelector('.vditor-sv').value.includes('![photo](reference/test.png)')")
  c.eval("document.getElementById('preview-mode').click()")
  wait("document.querySelector('.vditor-ir img')?.naturalWidth===256")
  c.eval('window.notebookFlush()')
  assert 'data:image' not in (folder/'main.md').read_text()
 print(f'PASS {theme} atomic selection, Backspace/Delete and cross-mode Undo')
click_image();key('Backspace',8);c.eval('window.notebookFlush()');time.sleep(5.3)
c.eval('window.notebookWake()')
assert not image.exists() and (folder/'.trash'/'test.png').exists()
key('z',90,2);wait("document.querySelector('.vditor-ir img')?.naturalWidth===256")
c.eval('window.notebookFlush()')
assert image.exists() and not (folder/'.trash'/'test.png').exists()
assert '![photo](reference/test.png)' in (folder/'main.md').read_text()
print('PASS image deletion recycles attachment after grace period; Undo restores bytes and reference')

# Native viewer failure must report an error without locking the note UI.
c.eval("window.failImageOpen=true;document.querySelector('.vditor-ir img').dispatchEvent(new MouseEvent('dblclick',{bubbles:true}))")
wait("document.getElementById('toast').textContent.includes('图片查看器不可用')")
assert not c.eval("!!document.querySelector('.vditor-img')")
c.eval("document.getElementById('source-mode').click()")
wait("document.querySelector('.vditor-sv')?.value.includes('![photo]')")
print('PASS viewer failure reports error and source mode remains accessible')

# Exercise the PWA viewer in a real separate browsing context. Its asynchronous
# read must reserve the popup synchronously, and release it on read failure.
viewer_source=Path('Notebook/src/image-viewer.mjs').read_text().replace('export async function','async function',1)
data=__import__('base64').b64encode(image.read_bytes()).decode()
expression="(async()=>{"+viewer_source+";const original=window.open;window.open=(...args)=>window.testViewer=original.apply(window,args);try{await viewBrowserImage('reference/test.png',async()=>"+json.dumps(data)+");return !!window.testViewer&&!window.testViewer.closed;}finally{window.open=original;}})()"
result=c.call('Runtime.evaluate',{'expression':expression,'returnByValue':True,'awaitPromise':True,'userGesture':True})
assert result['result']['value'] is True,result
wait("window.testViewer.document.querySelector('img')?.naturalWidth===256")
assert c.eval("window.testViewer.opener===null && window.testViewer.document.title==='test.png'")
c.eval('window.testViewer.close()')
assert c.eval('window.testViewer.closed')
expression="(async()=>{"+viewer_source+";const original=window.open;window.open=(...args)=>window.testViewer=original.apply(window,args);try{await viewBrowserImage('reference/missing.png',async()=>null);return false;}catch(e){return window.testViewer.closed&&e.message.includes('尚未下载');}finally{window.open=original;}})()"
result=c.call('Runtime.evaluate',{'expression':expression,'returnByValue':True,'awaitPromise':True,'userGesture':True})
assert result['result']['value'] is True,result
assert c.eval("document.querySelector('.vditor-sv')?.value.includes('![photo]')")
print('PASS separate web viewer displays original image, closes normally and cleans up failed reads')
