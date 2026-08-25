const requireEnv = (key: string): string => {
  const v = import.meta.env[key] as string | undefined;
  if (v == null) {
    throw new Error(
      `Missing required env var: ${key}. Copy frontend/.env.example to frontend/.env.local and fill in the value.`,
    );
  }
  return v;
};

export const API_BASE_URLS = {
  auth: requireEnv("VITE_AUTH_API_BASE_URL"),
  items: requireEnv("VITE_ITEM_API_BASE_URL"),
  matching: requireEnv("VITE_MATCHING_API_BASE_URL"),
  admin: requireEnv("VITE_ADMIN_API_BASE_URL"),
} as const;

export type ApiService = keyof typeof API_BASE_URLS;
