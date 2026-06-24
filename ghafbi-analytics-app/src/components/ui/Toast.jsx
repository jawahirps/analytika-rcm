export function Toast({ message }) {
  if (!message) return null;
  return (
    <div className="toast-message" role="status" aria-live="polite">
      {message}
    </div>
  );
}
