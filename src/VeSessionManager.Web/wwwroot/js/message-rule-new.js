// New message rule (#401): the chosen trigger decides three things on this form — whether there is a
// delay to set, what that delay is called, and which recipients are legal.
//
// All three are read from data attributes the server rendered, never restated here. A second copy of
// "which recipients does FccFeeOutstanding allow" is exactly the kind of duplicate that goes stale
// the first time MessageTriggerDefinitions changes, and goes stale silently: the form would offer a
// recipient the server then refuses, or hide one it would have accepted.
//
// Progressive enhancement, not a gate: with JavaScript unavailable every field is visible and the
// server validates the combination anyway (see MessageRuleAdminService.ValidateAsync). The worst case
// is a form that shows a delay box for a trigger with no delay, which the server ignores.
(function () {
  "use strict";

  var trigger = document.getElementById("triggerPicker");
  if (!trigger) return;

  var delayField = document.getElementById("delayField");
  var delayLabel = document.getElementById("delayLabel");
  var delayInput = delayField ? delayField.querySelector("input") : null;
  var delayCeiling = document.getElementById("delayCeiling");
  var recipient = document.getElementById("recipientPicker");

  // Rendered once per trigger by Razor, so this stays a lookup rather than a table to maintain.
  var prompts = {};
  var ceilings = {};
  var defaults = {};
  var takesParameter = {};
  Array.prototype.forEach.call(trigger.options, function (option) {
    var value = option.value;
    var blurb = document.querySelector('[data-trigger-blurb="' + value + '"]');
    if (blurb) {
      prompts[value] = blurb.getAttribute("data-prompt") || "";
      ceilings[value] = blurb.getAttribute("data-ceiling") || "";
      defaults[value] = blurb.getAttribute("data-default-days") || "";
      takesParameter[value] = blurb.getAttribute("data-takes-parameter") === "true";
    }
  });

  // The default this script last wrote into the delay box, so it can tell its own value from a typed
  // one. Seeded from what the server rendered, which is the arrival trigger's default.
  var lastAppliedDefault = delayInput ? delayInput.value : "";

  function apply() {
    var value = trigger.value;

    // One blurb visible at a time — the explanation of the moment you just picked.
    var blurbs = document.querySelectorAll("[data-trigger-blurb]");
    Array.prototype.forEach.call(blurbs, function (b) {
      b.hidden = b.getAttribute("data-trigger-blurb") !== value;
    });

    if (delayField) {
      var wanted = takesParameter[value];
      delayField.hidden = !wanted;
      // Disabled as well as hidden: a hidden input still posts, and a stray delay on a state trigger
      // would be stored as a number nothing reads.
      if (delayInput) delayInput.disabled = !wanted;
      if (wanted && delayLabel) delayLabel.textContent = prompts[value] || "Days";

      // Offer the trigger's own default rather than an empty box.
      //
      // Replaced only when the box is empty or still holds the default *this script* last put there —
      // so moving from a 1-day trigger to a 5-day one updates, while a number somebody typed
      // survives. "Only when empty" was the first attempt and left 1 sitting under a label reading
      // "days after the FCC entered the application", which is a wrong answer that looks deliberate.
      if (wanted && delayInput) {
        if (!delayInput.value || delayInput.value === lastAppliedDefault) {
          delayInput.value = defaults[value] || "";
        }
        lastAppliedDefault = defaults[value] || "";
      }
      if (delayCeiling) delayCeiling.textContent = ceilings[value] || "";
    }

    if (recipient) {
      // Hide the recipients this trigger cannot address, and move the selection off one that just
      // became illegal — leaving it selected would submit a value the server refuses.
      var selectedStillLegal = false;
      var firstLegal = null;
      Array.prototype.forEach.call(recipient.options, function (option) {
        var legal = (option.getAttribute("data-legal-for") || "").split(",").indexOf(value) !== -1;
        option.hidden = !legal;
        option.disabled = !legal;
        if (legal) {
          if (!firstLegal) firstLegal = option;
          if (option.selected) selectedStillLegal = true;
        }
      });
      if (!selectedStillLegal && firstLegal) firstLegal.selected = true;
    }
  }

  trigger.addEventListener("change", apply);
  apply();
})();
