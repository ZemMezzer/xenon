const corePages = [
    ["source-files", "Source files", "source-files.html"],
    ["namespaces", "Namespaces", "namespaces.html"],
    ["primitive-types", "Primitive types", "primitive-types.html"],
    ["literals-and-variables", "Literals and variables", "literals-and-variables.html"],
    ["const-and-readonly", "const and readonly", "const-and-readonly.html"],
    ["functions", "Functions", "functions.html"],
    ["access-modifiers", "Access modifiers", "access-modifiers.html"],
    ["operators", "Operators", "operators.html"],
    ["branching", "Branching", "branching.html"],
    ["loops", "Loops", "loops.html"],
    ["structs", "Structs", "structs.html"],
    ["inheritance", "Inheritance", "inheritance.html"],
    ["interfaces", "Interfaces", "interfaces.html"],
    ["abstraction", "Abstraction and virtual dispatch", "abstraction.html"],
    ["static-members", "Static members", "static-members.html"],
    ["properties-and-indexers", "Properties and indexers", "properties-and-indexers.html"],
    ["references", "References", "references.html"],
    ["arrays", "Arrays", "arrays.html"],
    ["pointers", "Raw pointers", "pointers.html"],
    ["casts", "Casts", "casts.html"],
    ["memory", "Manual memory", "memory.html"],
    ["type-layout", "Type layout", "type-layout.html"],
    ["c-interop", "C ABI", "c-interop.html"],
];

for (const sidebar of document.querySelectorAll("[data-doc-sidebar]")) {
    const back = document.createElement("a");
    back.className = "back-link";
    back.href = "index.html";
    back.textContent = "←  All documentation";

    const section = document.createElement("p");
    section.className = "sidebar-section";
    section.textContent = "Core";

    const navigation = document.createElement("div");
    navigation.className = "sidebar-nav";

    for (const [id, label, href] of corePages) {
        const link = document.createElement("a");
        link.href = href;
        link.textContent = label;
        if (sidebar.dataset.current === id) {
            link.className = "sidebar-current";
            link.setAttribute("aria-current", "page");
        }
        navigation.append(link);
    }

    sidebar.append(back, section, navigation);
}
