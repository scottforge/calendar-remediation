export function submitCompletion(form) {
    if (!(form instanceof HTMLFormElement) || !form.isConnected) {
        throw new Error("The sign-in form is no longer available.");
    }
    form.requestSubmit();
}
