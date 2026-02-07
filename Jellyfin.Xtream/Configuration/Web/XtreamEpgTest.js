export default function (view) {
  view.addEventListener("viewshow", () => {
    window.location.hash = "#!/configurationpage?name=XtreamSettings.html";
  });
}
