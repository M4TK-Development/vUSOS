FROM node:22-alpine AS base
RUN corepack enable && corepack prepare pnpm@10.28.0 --activate
WORKDIR /app
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml .npmrc tsconfig.base.json ./
COPY packages/shared/package.json packages/shared/
COPY apps/api/package.json apps/api/
COPY apps/web/package.json apps/web/
RUN pnpm install --frozen-lockfile

FROM base AS build
COPY packages ./packages
COPY apps ./apps
RUN pnpm -r build

FROM node:22-alpine AS runtime
RUN corepack enable && corepack prepare pnpm@10.28.0 --activate
WORKDIR /app
ENV NODE_ENV=production NODE_NO_WARNINGS=1
COPY --from=build /app/package.json /app/pnpm-workspace.yaml /app/pnpm-lock.yaml /app/.npmrc ./
COPY --from=build /app/packages/shared ./packages/shared
COPY --from=build /app/apps/api/package.json ./apps/api/
COPY --from=build /app/apps/api/dist ./apps/api/dist
COPY --from=build /app/apps/web/dist ./apps/web/dist
RUN pnpm install --frozen-lockfile --prod --filter @vusos/api --filter @vusos/shared
ENV PORT=3001 DATABASE_PATH=/data/vusos.sqlite STATIC_DIR=/app/apps/web/dist
VOLUME ["/data"]
EXPOSE 3001
CMD ["node", "apps/api/dist/server.js"]
