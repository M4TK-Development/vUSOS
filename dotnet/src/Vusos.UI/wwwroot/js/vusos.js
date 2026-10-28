/* vUSOS – interop JS współdzielony przez wszystkie hosty (Blazor Server, MAUI Blazor Hybrid). */
(function () {
  "use strict";

  var linkRef = null;
  var linkHandlerInstalled = false;

  function onDocumentClick(e) {
    if (!linkRef || e.defaultPrevented || e.button !== 0) return;
    var a = e.target && e.target.closest ? e.target.closest(".safe-html a[href]") : null;
    if (!a) return;
    var href = a.href;
    if (!/^(https?:|mailto:|tel:)/i.test(href)) return;
    e.preventDefault();
    linkRef.invokeMethodAsync("OpenExternal", href).catch(function () { /* obwód zamknięty */ });
  }

  window.vusos = {
    copyText: async function (text) {
      try {
        if (navigator.clipboard && window.isSecureContext) {
          await navigator.clipboard.writeText(text);
          return true;
        }
      } catch (e) { /* spadamy do metody zapasowej */ }
      try {
        var ta = document.createElement("textarea");
        ta.value = text;
        ta.setAttribute("readonly", "");
        ta.style.position = "fixed";
        ta.style.opacity = "0";
        document.body.appendChild(ta);
        ta.select();
        var ok = document.execCommand("copy");
        document.body.removeChild(ta);
        return ok;
      } catch (e) {
        return false;
      }
    },

    scrollIntoView: function (id, onlyNarrow) {
      if (onlyNarrow && !window.matchMedia("(max-width: 767.98px)").matches) return;
      var el = document.getElementById(id);
      if (el) el.scrollIntoView({ block: "start", behavior: "smooth" });
    },

    scrollTop: function () {
      window.scrollTo({ top: 0, behavior: "smooth" });
    },

    lockScroll: function (locked) {
      document.documentElement.classList.toggle("scroll-locked", !!locked);
    },

    focus: function (id) {
      var el = document.getElementById(id);
      if (el) el.focus({ preventScroll: true });
    },

    openUrl: function (url) {
      if (/^(mailto:|tel:)/i.test(url)) { window.location.href = url; return; }
      window.open(url, "_blank", "noopener,noreferrer");
    },

    registerLinkHandler: function (ref) {
      linkRef = ref || null;
      if (!linkHandlerInstalled) {
        document.addEventListener("click", onDocumentClick, true);
        linkHandlerInstalled = true;
      }
    }
  };

  window.vusosQuiz = {
    /** Zbiera pola formularza pytań z kontenera → [{name, value}]. Ostatnia wartość dla danej nazwy wygrywa (hidden "0" + checkbox "1"). */
    collect: function (containerId) {
      var root = document.getElementById(containerId);
      if (!root) return [];
      var result = [];
      var index = Object.create(null);
      function put(name, value) {
        if (name in index) { result[index[name]] = { name: name, value: value }; }
        else { index[name] = result.length; result.push({ name: name, value: value }); }
      }
      root.querySelectorAll("input, select, textarea").forEach(function (el) {
        if (!el.name || el.disabled) return;
        var tag = el.tagName;
        if (tag === "INPUT") {
          var type = (el.type || "text").toLowerCase();
          if (type === "radio" || type === "checkbox") { if (el.checked) put(el.name, el.value); }
          else if (type === "button" || type === "submit" || type === "reset" || type === "file" || type === "image") { /* pomijamy */ }
          else put(el.name, el.value);
        } else if (tag === "SELECT" && el.multiple) {
          Array.prototype.forEach.call(el.selectedOptions, function (o) { result.push({ name: el.name, value: o.value }); });
        } else {
          put(el.name, el.value);
        }
      });
      return result;
    },

    /** Przywraca zapisane odpowiedzi ([{name, value}]) do pól formularza. */
    restore: function (containerId, answers) {
      var root = document.getElementById(containerId);
      if (!root || !answers || !answers.length) return;
      var byName = Object.create(null);
      answers.forEach(function (a) { (byName[a.name] = byName[a.name] || []).push(String(a.value)); });
      root.querySelectorAll("input, select, textarea").forEach(function (el) {
        var values = byName[el.name];
        if (!el.name || !values) return;
        var tag = el.tagName;
        if (tag === "INPUT") {
          var type = (el.type || "text").toLowerCase();
          if (type === "radio") el.checked = values.indexOf(el.value) >= 0;
          else if (type === "checkbox") el.checked = values.some(function (v) { return v === el.value && v !== "0"; });
          else if (type === "hidden" || type === "button" || type === "submit" || type === "reset" || type === "file" || type === "image") { /* bez zmian */ }
          else el.value = values[values.length - 1];
        } else if (tag === "SELECT" && el.multiple) {
          Array.prototype.forEach.call(el.options, function (o) { o.selected = values.indexOf(o.value) >= 0; });
        } else {
          el.value = values[values.length - 1];
        }
      });
    }
  };
})();
