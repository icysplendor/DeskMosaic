"""Real wheel routing checks against isolated preview server and Chrome CDP."""
from pathlib import Path
import json,time,uuid,shutil
exec(Path(__file__).with_name('browser-checks.py').read_text().split('# Run against')[0])
c=CDP();c.call('Page.enable');c.call('Page.navigate',{'url':'about:blank'})
root=Path('/tmp/notebook-ui-test-library')
if root.exists():shutil.rmtree(root)
book,note=str(uuid.uuid4()),str(uuid.uuid4());folder=root/'books'/book/note
(folder/'reference').mkdir(parents=True);(folder/'.trash').mkdir()
(folder.parent/'book.json').write_text(json.dumps({'id':book,'title':'滚动测试','created':0}))
(folder/'note.json').write_text(json.dumps({'id':note,'title':'长代码笔记','created':0,'updated':0}))
md='正文开头\n\n```txt\n'+''.join(f'line {i}: code content\n' for i in range(250))+'```\n\n'+''.join(f'正文段落 {i}\n\n' for i in range(60))
(folder/'main.md').write_text(md);(root/'.state.json').write_text(json.dumps({'grace':5000,'lastPage':{'noteId':note,'bookId':book,'filter':'all'}}))
c.call('Page.addScriptToEvaluateOnNewDocument',{'source':"window.chrome=window.chrome||{};window.chrome.webview={postMessage:async message=>{try{const response=await fetch('/_bridge',{method:'POST',body:JSON.stringify(message)});const value=await response.json();window.nativeReply(message.id,value.result,value.error);}catch(error){window.nativeReply(message.id,null,error.message);}}};"})
c.call('Page.navigate',{'url':'http://127.0.0.1:4173/index.html'})
def wait(expr):
 end=time.monotonic()+8
 while time.monotonic()<end:
  if c.eval(expr):return
  time.sleep(.05)
 raise AssertionError(expr)
wait("document.querySelector('.vditor-ir__preview > code')?.textContent.includes('line 249')")
def wheel(x,y,dy=180,dx=0):
 c.call('Input.dispatchMouseEvent',{'type':'mouseWheel','x':x,'y':y,'deltaY':dy,'deltaX':dx});time.sleep(.18)
for width in (1920,900):
 c.call('Emulation.setDeviceMetricsOverride',{'width':width,'height':900,'deviceScaleFactor':1,'mobile':False})
 for theme in ('light','dark'):
  if c.eval('document.documentElement.dataset.theme')!=theme:c.eval("document.getElementById('theme').click()")
  c.eval("window.page=document.querySelector('.vditor-ir > .vditor-reset');window.code=document.querySelector('.vditor-ir__preview > code');page.scrollTop=0;code.scrollTop=0")
  assert c.eval('page.scrollHeight>page.clientHeight && code.scrollHeight>code.clientHeight')
  coords=c.eval("(()=>{let p=page.getBoundingClientRect(),f=page.parentElement.getBoundingClientRect(),b=code.getBoundingClientRect();return {left:(f.left+p.left)/2,right:(p.right+f.right)/2,y:b.top+40,cx:b.left+80};})()")
  wheel(coords['left'],coords['y']);assert c.eval('page.scrollTop>100 && code.scrollTop===0'),'left gutter must scroll page'
  c.eval('page.scrollTop=0');wheel(coords['right'],coords['y']);assert c.eval('page.scrollTop>100 && code.scrollTop===0'),'right gutter must scroll page'
  c.eval('page.scrollTop=0');wheel(coords['cx'],coords['y']);assert c.eval('code.scrollTop>100 && page.scrollTop===0'),'code body keeps native scrolling'
  c.eval('code.scrollTop=0;page.scrollTop=0')
  # Browser-native line/page deltas and zoom/sideways gestures.
  for mode in (1,2):
   c.eval('page.scrollTop=0;page.parentElement.dispatchEvent(new WheelEvent("wheel",{bubbles:true,cancelable:true,clientX:'+str(coords['left'])+',clientY:'+str(coords['y'])+',deltaY:1,deltaMode:'+str(mode)+'}))')
   assert c.eval('page.scrollTop>0 && code.scrollTop===0')
  for extra in ('ctrlKey:true','shiftKey:true','deltaX:200'):
   c.eval('page.scrollTop=0;page.parentElement.dispatchEvent(new WheelEvent("wheel",{bubbles:true,cancelable:true,clientX:'+str(coords['left'])+',clientY:'+str(coords['y'])+',deltaY:100,'+extra+'}))')
   assert c.eval('page.scrollTop===0'),'do not hijack modified/horizontal wheel'
  c.eval('page.scrollTop=page.scrollHeight');wheel(coords['left'],coords['y']);assert c.eval('page.scrollTop<=page.scrollHeight-page.clientHeight')
  before=c.eval('page.scrollTop');wheel(coords['right'],coords['y'],-180);assert c.eval('page.scrollTop')<before
 print(f'PASS {width}px light/dark gutters, code scroll, delta units and gesture exclusions')
c.eval("document.getElementById('source-mode').click()")
wait("document.querySelector('.vditor-sv').value.includes('line 249')")
time.sleep(.4)
c.eval("window.source=document.querySelector('.vditor-sv');source.scrollTop=0")
coords=c.eval("(()=>{let r=source.getBoundingClientRect();return {left:r.left+8,right:r.right-8,y:r.top+100}})()")
for side in ('left','right'):
 c.eval('source.scrollTop=0');wheel(coords[side],coords['y']);assert c.eval('source.scrollTop>100')
assert (folder/'main.md').read_text()==md,'wheel routing must not modify note content'
print('PASS source mode side gutters and unchanged Markdown')
