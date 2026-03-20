FROM node:23-alpine

WORKDIR /app

# Install dependencies first (layer-cached unless package files change)
COPY Server/package*.json ./
RUN npm ci --only=production

# Copy app source
COPY Server/src/ ./src/
COPY Server/public/ ./public/
COPY contracts/ ./contracts/

# Data directory for the SQLite database volume
RUN mkdir -p /app/data

EXPOSE 3000

CMD ["node", "src/index.js"]
