FROM node:22-alpine
RUN npm install -g @modelcontextprotocol/inspector@latest

# Bind on all interfaces so the published ports reach the server inside the
# container (HOST picks the address, DANGEROUSLY_BIND_ALL_INTERFACES confirms
# the intent), and skip the browser auto-open (no browser in here).
ENV HOST=0.0.0.0 \
    DANGEROUSLY_BIND_ALL_INTERFACES=true \
    MCP_AUTO_OPEN_ENABLED=false

EXPOSE 6274 6275
ENTRYPOINT ["mcp-inspector"]
