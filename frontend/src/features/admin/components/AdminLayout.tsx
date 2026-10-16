import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { AccountMenu } from "../../auth/components/AccountMenu";

const navItems = [
  { label: "User Management", to: "/admin/users", icon: "M15 19.128a9.38 9.38 0 002.625.372 9.337 9.337 0 004.121-.952 4.125 4.125 0 00-7.533-2.493M15 19.128v-.003c0-1.113-.285-2.16-.786-3.07M15 19.128v.106A12.318 12.318 0 018.624 21c-2.331 0-4.512-.645-6.374-1.766l-.001-.109a6.375 6.375 0 0111.964-3.07M12 6.375a3.375 3.375 0 11-6.75 0 3.375 3.375 0 016.75 0zm8.25 2.25a2.625 2.625 0 11-5.25 0 2.625 2.625 0 015.25 0z" },
  { label: "Match Appeals", to: "/admin/match-appeals", icon: "M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" },
  { label: "Spam Review", to: "/admin/spam-review", icon: "M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z" },
];


const LINK_BASE =
  "flex shrink-0 items-center gap-3 whitespace-nowrap rounded-lg px-3 py-2.5 text-sm font-medium transition-colors";

function linkClass({ isActive }: { isActive: boolean }) {
  return `${LINK_BASE} ${
    isActive ? "bg-indigo-50 text-indigo-700" : "text-gray-600 hover:bg-gray-50 hover:text-gray-900"
  }`;
}

function NavLinks() {
  return navItems.map((item) => (
    <NavLink key={item.to} to={item.to} className={linkClass}>
      <svg className="h-5 w-5 shrink-0" fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor">
        <path strokeLinecap="round" strokeLinejoin="round" d={item.icon} />
      </svg>
      {item.label}
    </NavLink>
  ));
}

function Brand() {
  return (
    <div className="flex items-center">
      <h1 className="text-lg font-bold text-indigo-600">back2u</h1>
      <span className="ml-2 rounded bg-indigo-100 px-1.5 py-0.5 text-[10px] font-semibold text-indigo-700 uppercase">Admin</span>
    </div>
  );
}

export function AdminLayout() {
  const navigate = useNavigate();

  return (
    <div className="flex h-screen bg-gray-100">
      {/* Sidebar (large screens) */}
      <aside className="hidden w-64 flex-col border-r border-gray-200 bg-white lg:flex">
        <div className="flex h-16 items-center px-6">
          <Brand />
        </div>

        <nav className="mt-4 flex-1 space-y-1 px-3">
          <NavLinks />
        </nav>
      </aside>

      {/* Main area */}
      <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
        {/* Header */}
        <header className="flex min-h-16 flex-wrap items-center justify-between gap-3 bg-white px-4 py-3 shadow-sm sm:px-6">
          <div className="flex items-center gap-4">
            <div className="lg:hidden">
              <Brand />
            </div>
            <h2 className="text-sm font-medium text-gray-500">Admin Dashboard</h2>
          </div>
          <div className="flex items-center gap-3 sm:gap-4">
            <button
              type="button"
              onClick={() => navigate("/")}
              className="whitespace-nowrap rounded-xl bg-indigo-600 px-3 py-2 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-indigo-700 sm:px-4"
            >
              Browse items on homepage
            </button>
            <AccountMenu />
          </div>
        </header>

        {/* Nav bar (small screens) */}
        <nav className="flex gap-1 overflow-x-auto border-b border-gray-200 bg-white px-3 py-2 lg:hidden">
          <NavLinks />
        </nav>

        {/* Page content */}
        <main className="flex-1 overflow-y-auto p-4 sm:p-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}