import {
  useEffect,
  useRef,
  useState,
} from "react";
import { useNavigate } from "react-router-dom";
import {
  ChevronDown,
  LogOut,
  User as UserIcon,
} from "lucide-react";
import { useAuth } from "../AuthContext";
import { useOptionalSolveRunner } from "../../admin/solve/solveRunnerContext";

const SOLVE_SIGN_OUT_WARNING =
  "A Solve is running. If you sign out, it stops, and the record stays Pending solve so it can be resumed. Sign out anyway?";

function initials(name: string): string {
  const parts = name.trim().split(/\s+/);
  const first = parts[0]?.[0] ?? "";
  const last =
    parts.length > 1
      ? parts[parts.length - 1][0]
      : "";

  return (first + last).toUpperCase();
}

export function AccountMenu() {
  const { user, logout } = useAuth();
  const navigate = useNavigate();
  const solveRunner = useOptionalSolveRunner();

  const [menuOpen, setMenuOpen] =
    useState(false);

  const menuRef =
    useRef<HTMLDivElement>(null);

  useEffect(() => {
    function onClickOutside(event: MouseEvent) {
      if (
        menuRef.current &&
        !menuRef.current.contains(
          event.target as Node,
        )
      ) {
        setMenuOpen(false);
      }
    }

    document.addEventListener(
      "mousedown",
      onClickOutside,
    );

    return () => {
      document.removeEventListener(
        "mousedown",
        onClickOutside,
      );
    };
  }, []);

  const handleLogout = async () => {
    const solving = Object.values(solveRunner?.runs ?? {}).some(
      (run) => run.phase === "running",
    );

    if (solving && !window.confirm(SOLVE_SIGN_OUT_WARNING)) {
      return;
    }

    await logout();
    navigate("/login", {
      replace: true,
    });
  };

  return (
    <div
      className="relative"
      ref={menuRef}
    >
      <button
        type="button"
        onClick={() =>
          setMenuOpen((value) => !value)
        }
        className="flex items-center gap-2 rounded-full py-1 pl-1 pr-2 transition-colors hover:bg-gray-100"
      >
        <span className="flex h-8 w-8 items-center justify-center rounded-full bg-indigo-100 text-xs font-bold text-indigo-700">
          {user ? (
            initials(user.name)
          ) : (
            <UserIcon className="h-4 w-4" />
          )}
        </span>

        <span className="text-sm font-medium text-gray-800">
          {user?.name ?? "Account"}
        </span>

        <ChevronDown
          className={`h-4 w-4 text-gray-400 transition-transform ${
            menuOpen ? "rotate-180" : ""
          }`}
        />
      </button>

      {menuOpen && (
        <div className="absolute right-0 top-full z-40 mt-2 w-48 overflow-hidden rounded-xl border border-gray-200 bg-white py-1 shadow-lg animate-fade-in">
          <button
            type="button"
            onClick={() => {
              setMenuOpen(false);
              navigate(user?.isAdmin ? "/admin/profile" : "/profile");
            }}
            className="flex w-full items-center gap-2 px-4 py-2 text-left text-sm text-gray-700 hover:bg-gray-50"
          >
            <UserIcon className="h-4 w-4" />
            Profile
          </button>

          <button
            type="button"
            onClick={handleLogout}
            className="flex w-full items-center gap-2 px-4 py-2 text-left text-sm text-red-600 hover:bg-red-50"
          >
            <LogOut className="h-4 w-4" />
            Sign out
          </button>
        </div>
      )}
    </div>
  );
}
