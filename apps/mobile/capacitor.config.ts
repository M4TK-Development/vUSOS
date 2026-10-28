import type { CapacitorConfig } from "@capacitor/cli";

const config: CapacitorConfig = {
  appId: "pl.m4tk.vusos",
  appName: "vUSOS",
  webDir: "../web/dist",
  server: {
    androidScheme: "https"
  },
  ios: {
    contentInset: "always",
    scheme: "vUSOS"
  },
  android: {
    allowMixedContent: false
  },
  plugins: {
    StatusBar: { overlaysWebView: false }
  }
};

export default config;
