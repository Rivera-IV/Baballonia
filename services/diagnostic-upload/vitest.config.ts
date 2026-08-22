import { cloudflareTest } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: "./wrangler.jsonc" },
      miniflare: {
        bindings: {
          MAX_STORED_BYTES: "1400",
          UPLOAD_QUOTA_JURISDICTION: "local",
        },
      },
    }),
  ],
  test: {
    include: ["test/**/*.spec.ts"],
  },
});
