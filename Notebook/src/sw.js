/* Build injects a content-hashed, immutable editor asset list. No authenticated API is cached. */
const CACHE = __CACHE_NAME__;
const ASSETS = __OFFLINE_ASSETS__;
self.addEventListener('install', event => event.waitUntil(caches.open(CACHE).then(cache=>cache.addAll(ASSETS))));
// New versions activate on the next launch, never halfway through editing.
self.addEventListener('activate', event => event.waitUntil((async()=> {
  for(const key of await caches.keys())if(key.startsWith('notes-shell-')&&key!==CACHE)await caches.delete(key);
  await self.clients.claim();
})()));
self.addEventListener('fetch', event => {
  const url=new URL(event.request.url);
  if(event.request.method!=='GET'||url.origin!==location.origin||url.pathname.startsWith('/api/')||url.pathname==='/health')return;
  event.respondWith((async()=> {
    const cache=await caches.open(CACHE),key=event.request.mode==='navigate'?'/index.html':url.pathname;
    const cached=await cache.match(key);if(cached)return cached;
    const response=await fetch(event.request);
    if(response.ok&&url.pathname.startsWith('/vendor/'))await cache.put(event.request,response.clone());
    return response;
  })());
});
