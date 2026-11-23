"""Import a realistic Chinese PDF name; open/save actions and portable Markdown.
Uses the same isolated preview server and Chrome CDP as image-browser-checks.py.
"""
from pathlib import Path
import json,time
exec(Path(__file__).with_name('image-browser-checks.py').read_text().split("for theme in")[0])
name='投标文件-上海氢安(+扫描版) [终版] 100%.pdf'
c.eval("window.actions=[];window.originalPost=window.chrome.webview.postMessage;window.chrome.webview.postMessage=m=>{if(['open','saveAttachment'].includes(m.op)){window.actions.push(m);window.nativeReply(m.id,false,null);}else window.originalPost(m)}")
c.eval("(()=>{let p=document.querySelector('.vditor-ir p');let r=document.createRange();r.selectNodeContents(p);r.collapse(false);getSelection().removeAllRanges();getSelection().addRange(r);document.querySelector('.vditor-ir [contenteditable=true]').focus();})()")
c.eval("(()=>{let d=new DataTransfer();d.items.add(new File(['%PDF-1.7\\noriginal bytes'],"+json.dumps(name)+",{type:'application/pdf'}));let f=document.getElementById('file-input');f.files=d.files;f.dispatchEvent(new Event('change'));})()")
wait("!!document.querySelector('.notebook-attachment-link')")
assert c.eval("document.querySelector('.notebook-attachment-link').textContent")==name
assert c.eval("[...document.querySelectorAll('[data-notebook-attachment] .vditor-ir__marker')].every(x=>x.getBoundingClientRect().width===0)")
r=c.eval("(()=>{let r=document.querySelector('.notebook-attachment-link').getBoundingClientRect();return {x:r.left+20,y:r.top+10}})()")
for typ in ('mousePressed','mouseReleased'):c.call('Input.dispatchMouseEvent',{'type':typ,'x':r['x'],'y':r['y'],'button':'left','clickCount':1})
wait("window.actions.some(x=>x.op==='open')")
for typ in ('mousePressed','mouseReleased'):c.call('Input.dispatchMouseEvent',{'type':typ,'x':r['x'],'y':r['y'],'button':'right','clickCount':1})
wait("!!document.querySelector('[role=menu]')")
c.eval("[...document.querySelectorAll('[role=menu] button')].find(x=>x.textContent.includes('另存为')).click()")
wait("window.actions.some(x=>x.op==='saveAttachment')")
assert c.eval("window.actions.find(x=>x.op==='saveAttachment').args.name")==name
assert '(+扫描版)' in c.eval("window.actions.find(x=>x.op==='open').args.path")
c.eval("document.getElementById('attachments').click()")
wait("[...document.querySelectorAll('.attachment-row')].some(x=>x.textContent.includes('投标文件'))")
c.eval("[...document.querySelectorAll('.attachment-row')].find(x=>x.textContent.includes('投标文件')).querySelectorAll('button')[1].click()")
wait("window.actions.filter(x=>x.op==='saveAttachment').length===2")
c.eval("document.getElementById('source-mode').click()")
source=c.eval("document.querySelector('.vditor-sv').value")
assert '%28' in source and '%29' in source and 'data-notebook' not in source and '<a' not in source
c.eval("document.getElementById('preview-mode').click()")
wait("!!document.querySelector('.notebook-attachment-link')")
c.eval('window.notebookFlush()')
assert next((folder/'reference').glob('*.pdf')).read_bytes()==b'%PDF-1.7\noriginal bytes'
print('PASS Chinese PDF import, visible filename, hidden path markers, open, context/panel save-as and portable Markdown')
