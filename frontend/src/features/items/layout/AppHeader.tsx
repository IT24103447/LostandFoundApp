import {
  NavLink,
  useNavigate,
} from "react-router-dom";
import { MapPin } from "lucide-react";
import { useAuth } from "../../auth/AuthContext";
import { AccountMenu } from "../../auth/components/AccountMenu";

const NAV_LINKS = [
  {
    label: "Home",
    to: "/",
  },
  {
    label: "My Reports",
    to: "/my-reports",
  },
  {
    label: "Matched Items",
    to: "/matched-items",
  },
];

const ADMIN_NAV_LINKS = [
  {
    label: "Home",
    to: "/",
  },
];

export function AppHeader() {
  const { user } = useAuth();
  const navigate = useNavigate();

  return (
    <header className="sticky top-0 z-30 border-b border-gray-200 bg-white/90 backdrop-blur-sm">
      <div className="mx-auto flex h-[72px] max-w-[1600px] items-center gap-8 px-6 lg:px-10">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-full bg-gradient-to-br from-indigo-600 to-purple-600 text-white shadow-sm">
            <MapPin
              className="h-5 w-5"
              strokeWidth={2.25}
            />
          </div>

          <span className="text-xl font-extrabold tracking-tight text-gray-900">
            back2u
          </span>
        </div>

        <nav className="flex items-center gap-8">
          {(user?.isAdmin ? ADMIN_NAV_LINKS : NAV_LINKS).map((link) => (
            <NavLink
              key={link.to}
              to={link.to}
              end={link.to === "/"}
              className={({ isActive }) =>
                `relative py-1 text-[15px] font-medium transition-colors ${
                  isActive
                    ? "text-indigo-600 after:absolute after:-bottom-[27px] after:left-0 after:h-[2px] after:w-full after:bg-indigo-600"
                    : "text-gray-500 hover:text-gray-900"
                }`
              }
            >
              {link.label}
            </NavLink>
          ))}
        </nav>

        <div className="ml-auto flex items-center gap-5">
          {user?.isAdmin && (
            <button
              type="button"
              onClick={() => navigate("/admin/dashboard")}
              className="rounded-xl bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-indigo-700"
            >
              Go to admin dashboard
            </button>
          )}

          <AccountMenu />
        </div>
      </div>
    </header>
  );
}
