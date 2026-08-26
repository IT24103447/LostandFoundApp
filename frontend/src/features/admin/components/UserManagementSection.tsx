import { useCallback, useEffect, useState } from "react";
import {
  getUsers,
  kickUser,
  unkickUser,
  type AdminUser,
  type UsersParams,
} from "../api/admin";

export function UserManagementSection() {
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [total, setTotal] = useState(0);
  const [totalPages, setTotalPages] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [search, setSearch] = useState("");
  const [filterKicked, setFilterKicked] = useState<boolean | undefined>(undefined);
  const [filterVerified, setFilterVerified] = useState<boolean | undefined>(undefined);
  const [page, setPage] = useState(1);

  const [confirmDialog, setConfirmDialog] = useState<{
    userId: string;
    userName: string;
    action: "kick" | "unkick";
  } | null>(null);
  const [actionLoading, setActionLoading] = useState(false);

  const fetchUsers = useCallback(async (params: UsersParams) => {
    setLoading(true);
    setError(null);
    try {
      const res = await getUsers(params);
      setUsers(res.users);
      setTotal(res.total);
      setTotalPages(res.totalPages);
    } catch {
      setError("Failed to load users.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchUsers({ search: search || undefined, isKicked: filterKicked, isVerified: filterVerified, page });
  }, [search, filterKicked, filterVerified, page, fetchUsers]);

  const handleConfirm = async () => {
    if (!confirmDialog) return;
    setActionLoading(true);
    try {
      if (confirmDialog.action === "kick") {
        await kickUser(confirmDialog.userId);
      } else {
        await unkickUser(confirmDialog.userId);
      }
      setConfirmDialog(null);
      fetchUsers({ search: search || undefined, isKicked: filterKicked, isVerified: filterVerified, page });
    } catch {
      setError("Action failed. Please try again.");
    } finally {
      setActionLoading(false);
    }
  };

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold text-gray-900">User Management</h1>

      {/* Filters */}
      <div className="flex flex-wrap gap-4">
        <input
          type="text"
          placeholder="Search by name or email…"
          value={search}
          onChange={(e) => { setSearch(e.target.value); setPage(1); }}
          className="flex-1 min-w-[200px] rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 outline-none"
        />
        <select
          value={filterKicked === undefined ? "" : String(filterKicked)}
          onChange={(e) => {
            setFilterKicked(e.target.value === "" ? undefined : e.target.value === "true");
            setPage(1);
          }}
          className="rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 outline-none"
        >
          <option value="">All statuses</option>
          <option value="false">Active</option>
          <option value="true">Kicked</option>
        </select>
        <select
          value={filterVerified === undefined ? "" : String(filterVerified)}
          onChange={(e) => {
            setFilterVerified(e.target.value === "" ? undefined : e.target.value === "true");
            setPage(1);
          }}
          className="rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 outline-none"
        >
          <option value="">All verification</option>
          <option value="true">Verified</option>
          <option value="false">Unverified</option>
        </select>
      </div>

      {error && (
        <div className="rounded-lg bg-red-50 border border-red-200 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}

      {/* User Table */}
      <div className="bg-white shadow rounded-lg overflow-hidden">
        <table className="min-w-full divide-y divide-gray-200">
          <thead className="bg-gray-50">
            <tr>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Name</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Email</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Phone</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Verified</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Role</th>
              <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
            </tr>
          </thead>
          <tbody className="bg-white divide-y divide-gray-200">
            {loading ? (
              <tr>
                <td colSpan={7} className="px-6 py-12 text-center text-sm text-gray-500">Loading…</td>
              </tr>
            ) : users.length === 0 ? (
              <tr>
                <td colSpan={7} className="px-6 py-12 text-center text-sm text-gray-500">No users found.</td>
              </tr>
            ) : (
              users.map((u) => (
                <tr key={u.id} className={u.isKicked ? "bg-red-50" : ""}>
                  <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">{u.name}</td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{u.email}</td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{u.phoneNo}</td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm">
                    {u.isEmailVerified ? (
                      <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">Verified</span>
                    ) : (
                      <span className="inline-flex items-center rounded-full bg-yellow-100 px-2.5 py-0.5 text-xs font-medium text-yellow-800">Unverified</span>
                    )}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm">
                    {u.isKicked ? (
                      <span className="inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800">Kicked</span>
                    ) : (
                      <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">Active</span>
                    )}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                    {u.isAdmin ? "Admin" : "User"}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-right text-sm">
                    {!u.isAdmin && (
                      u.isKicked ? (
                        <button
                          onClick={() => setConfirmDialog({ userId: u.id, userName: u.name, action: "unkick" })}
                          className="text-indigo-600 hover:text-indigo-500 font-medium"
                        >
                          Unkick
                        </button>
                      ) : (
                        <button
                          onClick={() => setConfirmDialog({ userId: u.id, userName: u.name, action: "kick" })}
                          className="text-red-600 hover:text-red-500 font-medium"
                        >
                          Kick
                        </button>
                      )
                    )}
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-gray-500">
            Showing {(page - 1) * 20 + 1}–{Math.min(page * 20, total)} of {total}
          </p>
          <div className="flex gap-2">
            <button
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={page === 1}
              className="rounded-lg border border-gray-300 px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Previous
            </button>
            <button
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              disabled={page === totalPages}
              className="rounded-lg border border-gray-300 px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Next
            </button>
          </div>
        </div>
      )}

      {/* Confirmation Dialog */}
      {confirmDialog && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
          <div className="bg-white rounded-lg shadow-xl max-w-sm w-full mx-4 p-6">
            <h3 className="text-lg font-semibold text-gray-900 mb-2">
              {confirmDialog.action === "kick" ? "Kick User" : "Unkick User"}
            </h3>
            <p className="text-sm text-gray-600 mb-6">
              {confirmDialog.action === "kick"
                ? `Are you sure you want to kick "${confirmDialog.userName}"? They will be immediately blocked from the platform.`
                : `Are you sure you want to restore "${confirmDialog.userName}"? They will regain access to the platform.`}
            </p>
            <div className="flex justify-end gap-3">
              <button
                onClick={() => setConfirmDialog(null)}
                disabled={actionLoading}
                className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                onClick={handleConfirm}
                disabled={actionLoading}
                className={`rounded-lg px-4 py-2 text-sm font-medium text-white disabled:opacity-50 ${
                  confirmDialog.action === "kick"
                    ? "bg-red-600 hover:bg-red-700"
                    : "bg-indigo-600 hover:bg-indigo-700"
                }`}
              >
                {actionLoading ? "Processing…" : confirmDialog.action === "kick" ? "Kick" : "Unkick"}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
