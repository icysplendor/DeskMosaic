FROM node:22-alpine
WORKDIR /app
COPY Notebook/server ./server
COPY Notebook/NotebookWeb ./NotebookWeb
USER node
ENV HOST=0.0.0.0 PORT=8080 NOTES_CONFIG_FILE=/data/settings.enc
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s CMD node -e "fetch('http://127.0.0.1:8080/health').then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))"
CMD ["node", "server/server.mjs"]
