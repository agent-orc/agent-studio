# Container deployment

Run `npm ci`, `npm test`, and `npm run build`. Build with `docker build -t service .`, then publish the image to your registry. The container listens on port 3000 and exposes `/health`.
