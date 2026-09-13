import { createContext, useContext, useState, useEffect, useCallback, type ReactNode } from "react";
import type { UserProfile } from "./api/auth";
import { getMe, logout as logoutApi, login as loginApi } from "./api/auth";
import { setOnAuthFailure, setApiAuthToken } from "../../lib/apiClient";

type AuthContextType = {
  user: UserProfile | null;
  isLoading: boolean;
  isAuthenticated: boolean;
  login: (email: string, password: string) => Promise<UserProfile>;
  logout: () => Promise<void>;
  setUser: (user: UserProfile) => void;
  refreshUser: () => Promise<UserProfile | null>;
};

const AuthContext = createContext<AuthContextType>({
  user: null,
  isLoading: true,
  isAuthenticated: false,
  login: async () => { throw new Error("AuthProvider not mounted"); },
  logout: async () => {},
  setUser: () => {},
  refreshUser: async () => null,
});

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserProfile | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    const restore = async () => {
      try {
        const profile = await getMe();
        // /me returns a fresh JWT — set it synchronously so any cross-service call made
        // by a child component (mounted only after isLoading becomes false) already has it.
        if (profile.token) setApiAuthToken(profile.token);
        if (!cancelled) setUser(profile);
      } catch {
        if (!cancelled) setUser(null);
      } finally {
        if (!cancelled) setIsLoading(false);
      }
    };
    restore();
    return () => {
      cancelled = true;
    };
  }, []);

  const login = async (email: string, password: string): Promise<UserProfile> => {
    const profile = await loginApi({ email, password });
    if (profile.token) setApiAuthToken(profile.token);
    setUser(profile);
    return profile;
  };

  const logout = async () => {
    try {
      await logoutApi();
    } catch {
      // ignore logout errors — clear local state regardless
    }
    setApiAuthToken(null);
    setUser(null);
  };

  const handleKicked = useCallback(() => {
    logout();
    window.location.href = "/login";
  }, []);

  useEffect(() => {
    setOnAuthFailure(handleKicked);
    return () => setOnAuthFailure(null);
  }, [handleKicked]);

  useEffect(() => {
    if (!user) return;
    const interval = setInterval(async () => {
      try {
        const profile = await getMe();
        // Refresh the in-memory bearer token on the kick-detection poll so it never
        // expires while the tab is open.
        if (profile.token) setApiAuthToken(profile.token);
      } catch (err) {
        const status = typeof err === "object" && err !== null && "status" in err
          ? (err as { status: number }).status
          : 0;
        if (status === 403) {
          handleKicked();
        }
      }
    }, 30_000);
    return () => clearInterval(interval);
  }, [user, handleKicked]);

  const refreshUser = async (): Promise<UserProfile | null> => {
    try {
      const profile = await getMe();
      if (profile.token) setApiAuthToken(profile.token);
      setUser(profile);
      return profile;
    } catch {
      setUser(null);
      return null;
    }
  };

  return (
    <AuthContext.Provider
      value={{
        user,
        isLoading,
        isAuthenticated: !!user,
        login,
        logout,
        setUser,
        refreshUser,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export const useAuth = () => useContext(AuthContext);
