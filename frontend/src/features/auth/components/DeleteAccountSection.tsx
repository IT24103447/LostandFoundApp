import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../AuthContext";
import { deleteAccount } from "../api/auth";
import type { ApiError } from "../../../lib/apiClient";
import { inputClass } from "./helpers";

export function DeleteAccountSection() {
  const navigate = useNavigate();
  const { logout } = useAuth();
  const [password, setPassword] = useState("");
  const [showConfirm, setShowConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isDeleting, setIsDeleting] = useState(false);

  const handleDelete = async () => {
    setError(null);
    setIsDeleting(true);
    try {
      await deleteAccount({ password });
      await logout();
      navigate("/login", { replace: true });
    } catch (err) {
      const apiErr = err as ApiError;
      if (apiErr.status === 400) {
        const body = apiErr.body as { error?: string } | null;
        setError(body?.error ?? "Incorrect password.");
      } else if (apiErr.status === 403) {
        const body = apiErr.body as { error?: string } | null;
        setError(body?.error ?? "Admin accounts cannot be deleted.");
      } else {
        setError("Something went wrong. Please try again.");
      }
    } finally {
      setIsDeleting(false);
    }
  };

  return (
    <div className="rounded-lg border border-red-200 bg-red-50 p-6">
      <h3 className="text-sm font-semibold text-red-800">Delete account</h3>
      <p className="mt-1 text-sm text-red-700">
        Permanently delete your account and all associated data. This action
        cannot be undone.
      </p>

      <div className="mt-4">
        <label htmlFor="delete-password" className="block text-sm font-medium text-red-800">
          Confirm your password
        </label>
        <input
          id="delete-password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => {
            setPassword(e.target.value);
            setError(null);
            setShowConfirm(false);
          }}
          placeholder="Enter your password"
          className={`${inputClass(!!error)} mt-1`}
        />
      </div>

      {error && (
        <p className="mt-2 text-sm text-red-600 font-medium" role="alert">
          {error}
        </p>
      )}

      <div className="mt-4">
        {showConfirm ? (
          <div className="flex items-center gap-3">
            <button
              onClick={handleDelete}
              disabled={isDeleting || !password}
              className="rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {isDeleting ? "Deleting…" : "Yes, delete my account"}
            </button>
            <button
              onClick={() => setShowConfirm(false)}
              disabled={isDeleting}
              className="rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 transition-colors"
            >
              Cancel
            </button>
          </div>
        ) : (
          <button
            onClick={() => setShowConfirm(true)}
            disabled={!password}
            className="rounded-lg border border-red-300 bg-white px-4 py-2 text-sm font-medium text-red-700 shadow-sm hover:bg-red-50 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            Delete account
          </button>
        )}
      </div>

      {showConfirm && (
        <p className="mt-3 text-xs text-red-600">
          Type your password and click "Yes, delete my account" to confirm.
        </p>
      )}
    </div>
  );
}
